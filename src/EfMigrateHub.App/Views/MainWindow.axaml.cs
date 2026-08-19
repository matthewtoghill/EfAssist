using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using AvaloniaEdit.Document;
using AvaloniaEdit.Search;
using EfMigrateHub.App.ViewModels;

namespace EfMigrateHub.App.Views;

public partial class MainWindow : Window
{
    /// <summary>The view model whose Script tab the SQL editor is currently showing.</summary>
    private ScriptViewModel? _script;

    /// <summary>The view model behind the migration detail pane.</summary>
    private MigrationDetailViewModel? _detail;

    public MainWindow()
    {
        InitializeComponent();

        // Ctrl+F, Ctrl+H and F3 over the SQL. Read-only, so replace does nothing, but the search
        // half is the point for a long script.
        SearchPanel.Install(SqlEditor);
        SearchPanel.Install(DetailEditor);

        // A definition holds literal colours, so a theme switch needs a different one. This fires
        // for a System user whose OS flips too, which a Theme-property handler would miss.
        SqlEditor.ActualThemeVariantChanged += (_, _) => ApplySqlHighlighting();
        DetailEditor.ActualThemeVariantChanged += (_, _) => ApplyDetailHighlighting();
        ApplySqlHighlighting();
        ApplyDetailHighlighting();

        // The view owns the TopLevel that file pickers and the clipboard need, so it supplies those
        // to the view model rather than the view model reaching for UI services.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }

            viewModel.PickSolutionAsync = PickSolutionAsync;
            viewModel.PickFolderAsync = PickFolderAsync;
            viewModel.CopyToClipboardAsync = CopyToClipboardAsync;
            viewModel.ConfirmAsync = ConfirmAsync;
            viewModel.ShowErrorAsync = ShowErrorAsync;
            viewModel.Script.PickSaveFileAsync = PickSaveFileAsync;
            viewModel.Script.PickFolderAsync = PickFolderAsync;
            viewModel.Script.OpenFileAsync = path => OpenWithShellAsync(path, reveal: false);
            viewModel.Script.RevealFileAsync = path => OpenWithShellAsync(path, reveal: true);
            viewModel.Migrations.Detail.OpenFileAsync = path => OpenWithShellAsync(path, reveal: false);

            viewModel.Session.Output.CollectionChanged += ScrollOutputToEnd;

            if (_script is not null)
            {
                _script.PropertyChanged -= OnScriptPropertyChanged;
            }

            _script = viewModel.Script;
            _script.PropertyChanged += OnScriptPropertyChanged;
            ShowSql();

            if (_detail is not null)
            {
                _detail.PropertyChanged -= OnDetailPropertyChanged;
            }

            _detail = viewModel.Migrations.Detail;
            _detail.PropertyChanged += OnDetailPropertyChanged;
            ShowDetail();
        };
    }

    private void ApplySqlHighlighting() =>
        SqlEditor.SyntaxHighlighting = SyntaxHighlighting.Sql(SqlEditor.ActualThemeVariant);

    /// <summary>
    /// The detail editor shows two languages, so which definition it wants depends on the view model
    /// as well as on the theme variant.
    /// </summary>
    private void ApplyDetailHighlighting() =>
        DetailEditor.SyntaxHighlighting = _detail?.IsShowingSql == true
            ? SyntaxHighlighting.Sql(DetailEditor.ActualThemeVariant)
            : SyntaxHighlighting.CSharp(DetailEditor.ActualThemeVariant);

    private void OnScriptPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScriptViewModel.Sql))
        {
            ShowSql();
        }
    }

    /// <summary>
    /// Pushes the generated SQL into the editor. A fresh document rather than assigning Text, so the
    /// caret, selection and undo stack from the previous script do not carry over into the new one.
    /// </summary>
    private void ShowSql() =>
        SqlEditor.Document = new TextDocument(_script?.Sql ?? "");

    private void OnDetailPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MigrationDetailViewModel.Text))
        {
            ShowDetail();
        }
        else if (e.PropertyName == nameof(MigrationDetailViewModel.IsShowingSql))
        {
            ApplyDetailHighlighting();
        }
    }

    /// <summary>
    /// Pushes the selected migration's source or SQL into the detail editor, on the same fresh-document
    /// basis as <see cref="ShowSql"/> so nothing carries over from the previous migration.
    /// </summary>
    private void ShowDetail()
    {
        ApplyDetailHighlighting();
        DetailEditor.Document = new TextDocument(_detail?.Text ?? "");
    }

    private void ScrollOutputToEnd(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            OutputScroller.ScrollToEnd();
        }
    }

    private async Task<string?> PickSolutionAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a solution or project",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Solutions and projects")
                {
                    Patterns = ["*.slnx", "*.sln", "*.csproj", "*.fsproj", "*.vbproj"],
                },
            ],
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open a folder containing a solution",
            AllowMultiple = false,
        });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>
    /// Modal, and owned by this window, so a destructive action cannot be triggered while its
    /// warning is on screen.
    /// </summary>
    private Task<bool> ConfirmAsync(ConfirmRequest request) =>
        new ConfirmWindow(request).ShowDialog<bool>(this);

    private Task ShowErrorAsync(ErrorDetail detail) =>
        new ErrorWindow(detail).ShowDialog(this);

    private async Task<string?> PickSaveFileAsync(string suggestedName, string? startFolder)
    {
        var options = new FilePickerSaveOptions
        {
            Title = "Save SQL script",
            SuggestedFileName = suggestedName,
            DefaultExtension = "sql",
            // The OS dialog does its own overwrite prompt, which is why the app only asks when
            // writing straight into a configured scripts folder.
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("SQL script") { Patterns = ["*.sql"] }],
        };

        if (startFolder is not null)
        {
            options.SuggestedStartLocation =
                await StorageProvider.TryGetFolderFromPathAsync(startFolder);
        }

        var file = await StorageProvider.SaveFilePickerAsync(options);
        return file?.TryGetLocalPath();
    }

    /// <summary>
    /// Hands the path to the OS: open the file with whatever is registered for .sql, or open its
    /// folder. <c>UseShellExecute</c> is what makes that work rather than trying to exec the file.
    /// </summary>
    private static Task OpenWithShellAsync(string path, bool reveal)
    {
        // ponytail: Explorer's /select is Windows-only, which matches the v1 publish target. Other
        // platforms fall back to opening the containing folder, which is the useful part anyway.
        var startInfo = reveal
            ? OperatingSystem.IsWindows()
                ? new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                : new ProcessStartInfo(System.IO.Path.GetDirectoryName(path) ?? path) { UseShellExecute = true }
            : new ProcessStartInfo(path) { UseShellExecute = true };

        try
        {
            Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Nothing sensible to do from the view; the path is still shown in the status bar.
        }

        return Task.CompletedTask;
    }

    private async Task CopyToClipboardAsync(string text)
    {
        IClipboard? clipboard = Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
