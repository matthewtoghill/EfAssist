using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using AvaloniaEdit.Document;
using AvaloniaEdit.Search;
using EfAssist.App.ViewModels;
using EfAssist.Core;

namespace EfAssist.App.Views;

public partial class MainWindow : Window
{
    /// <summary>The view model whose Script tab the SQL editor is currently showing.</summary>
    private ScriptViewModel? _script;

    /// <summary>The view model behind the migration detail pane.</summary>
    private MigrationDetailViewModel? _detail;

    /// <summary>
    /// The confirmation currently on screen, if any. Only the SQL preview needs it, to own its own
    /// window and to know whether the question it belongs to is still being asked.
    /// </summary>
    private ConfirmWindow? _confirm;

    /// <summary>
    /// The console row's height while expanded, so folding it away and back does not throw away a
    /// height the user set with the splitter.
    /// </summary>
    private GridLength _outputHeight = new(200);

    public MainWindow()
    {
        InitializeComponent();

        // Ctrl+F, Ctrl+H and F3 over the SQL. Read-only, so replace does nothing, but the search
        // half is the point for a long script.
        SearchPanel.Install(SqlEditor);
        SearchPanel.Install(DetailEditor);

        // Subscribed here rather than in DataContextChanged, which can fire more than once.
        Closing += OnClosing;

        // The top bar carries three pickers, the environment summary and five buttons, which do not
        // all fit at the 900px minimum width. There is no container query, so the one class the
        // styles key off is set here on resize.
        SizeChanged += (_, e) => ApplyTopBarDensity(e.NewSize.Width);
        ApplyTopBarDensity(Width);

        WireDiagramSurface();

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

            RestoreWindowLayout(viewModel.WindowLayout);

            viewModel.PickSolutionAsync = PickSolutionAsync;
            viewModel.PickFolderAsync = PickFolderAsync;
            viewModel.CopyToClipboardAsync = CopyToClipboardAsync;
            viewModel.ConfirmAsync = ConfirmAsync;
            viewModel.ShowErrorAsync = ShowErrorAsync;
            viewModel.ShowSettingsAsync = ShowSettingsAsync;
            viewModel.RestartRequested = Restart;
            viewModel.Script.PickSaveFileAsync = PickSaveFileAsync;
            viewModel.Script.PickFolderAsync = PickFolderAsync;
            viewModel.ShowFolderAsync = path => OpenWithShellAsync(path, reveal: false);
            viewModel.Script.OpenFileAsync = path => OpenWithShellAsync(path, reveal: false);
            viewModel.Script.RevealFileAsync = path => OpenWithShellAsync(path, reveal: true);
            viewModel.Migrations.Detail.OpenFileAsync = path => OpenWithShellAsync(path, reveal: false);
            viewModel.Migrations.ShowSqlPreviewAsync = ShowSqlPreviewAsync;

            // Layout sizes nodes from measured text, which Core cannot do — it has no Avalonia
            // reference and no font. Without this it falls back to a character-count approximation
            // and nodes come out slightly too narrow or too wide.
            viewModel.Diagrams.PickSaveFileAsync = PickSaveFileAsync;
            viewModel.Diagrams.MeasureText = DiagramTheme.Measure(FontFamily, DiagramView.RowFontFamily);
            viewModel.Diagrams.CentreOn = DiagramView.CentreOn;
            viewModel.Diagrams.FitToWindow = DiagramView.FitToWindow;

            viewModel.Session.Output.CollectionChanged += ScrollOutputToEnd;

            // The expander folds its own content, but the row it sits in still has to give the
            // height back — a fixed row would leave the fold showing as empty space.
            viewModel.PropertyChanged += OnMainPropertyChanged;
            ApplyOutputHeight(viewModel.OutputExpanded);

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
    /// Connects the diagram surface to the view model. Done in code rather than XAML because the
    /// surface raises plain events rather than commands — a pointer drag is a stream of positions,
    /// not something a <c>CommandParameter</c> can carry.
    /// </summary>
    private void WireDiagramSurface()
    {
        DiagramZoomIn.Click += (_, _) => DiagramView.ZoomBy(1.2);
        DiagramZoomOut.Click += (_, _) => DiagramView.ZoomBy(1 / 1.2);
        DiagramZoomReset.Click += (_, _) => DiagramView.ResetView();
        DiagramFit.Click += (_, _) => DiagramView.FitToWindow();

        DiagramView.SelectionRequested += (_, entity) =>
            (DataContext as MainWindowViewModel)?.Diagrams.Select(entity);

        DiagramView.NodeMoved += (_, move) =>
            (DataContext as MainWindowViewModel)?.Diagrams.MoveNode(move.Entity, move.Position);

        // Saving on every pointer move would write the file dozens of times per drag. Once the
        // pointer is up, the position is what the user meant.
        DiagramView.PointerReleased += (_, _) =>
            (DataContext as MainWindowViewModel)?.Diagrams.CommitMove();
    }

    /// <summary>
    /// Puts the window back where it was closed. Runs from <c>DataContextChanged</c>, which fires
    /// before the window is shown, so nothing jumps on screen.
    /// </summary>
    private void RestoreWindowLayout(WindowSettings layout)
    {
        // A remembered position is only used when it still lands on a connected screen: restoring
        // onto a monitor that has since been unplugged leaves a window that cannot be reached.
        if (layout is { Width: > 0, Height: > 0, X: { } x, Y: { } y }
            && Screens.ScreenFromPoint(new PixelPoint(x, y)) is not null)
        {
            Width = layout.Width.Value;
            Height = layout.Height.Value;
            Position = new PixelPoint(x, y);
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        if (layout.Maximised)
        {
            WindowState = Avalonia.Controls.WindowState.Maximized;
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var state = WindowState;

        // Minimised says nothing about how the user wants the window, and Windows reports an
        // off-screen position for it, so there is nothing here worth writing down.
        if (state == Avalonia.Controls.WindowState.Minimized)
        {
            return;
        }

        viewModel.SaveWindowLayout(
            maximised: state == Avalonia.Controls.WindowState.Maximized,
            bounds: state == Avalonia.Controls.WindowState.Normal
                ? (Position.X, Position.Y, ClientSize.Width, ClientSize.Height)
                : null);
    }

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

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.OutputExpanded)
            && sender is MainWindowViewModel viewModel)
        {
            ApplyOutputHeight(viewModel.OutputExpanded);
        }
    }

    /// <summary>
    /// Adds or removes the top bar's "narrow" class, which drops the picker captions and the
    /// environment summary. The threshold is where the full bar stops fitting at the shipped font
    /// size; the pickers themselves never hide, since they are the point of the bar.
    /// </summary>
    private void ApplyTopBarDensity(double width)
    {
        const double NarrowBelow = 1180;

        if (width < NarrowBelow)
        {
            TopBar.Classes.Add("narrow");
            return;
        }

        TopBar.Classes.Remove("narrow");
    }

    private void ApplyOutputHeight(bool expanded)
    {
        var row = MainPane.RowDefinitions[2];

        if (expanded)
        {
            row.Height = _outputHeight;
            return;
        }

        // Remember what the splitter left it at before collapsing to the header bar.
        _outputHeight = row.Height;
        row.Height = GridLength.Auto;
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
    private async Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        // Held while the dialog is up so a SQL preview can be owned by it and stack above it. A
        // field rather than a parameter because the preview is requested by the view model, which
        // knows nothing about windows.
        var window = new ConfirmWindow(request);
        _confirm = window;

        try
        {
            return await window.ShowDialog<bool>(this);
        }
        finally
        {
            _confirm = null;
        }
    }

    /// <summary>
    /// Shows generated SQL over the confirmation that asked for it.
    /// </summary>
    /// <remarks>
    /// Does nothing if that confirmation has already been answered — the user can cancel while the
    /// script is still generating, and a preview window opening after the question has gone would be
    /// a surprise attached to nothing. The status line still names the file it was written to.
    /// </remarks>
    private async Task ShowSqlPreviewAsync(SqlPreviewRequest request)
    {
        if (_confirm is null)
        {
            return;
        }

        await new SqlPreviewWindow(request, path => OpenWithShellAsync(path, reveal: false))
            .ShowDialog(_confirm);
    }

    private Task ShowErrorAsync(ErrorDetail detail) =>
        new ErrorWindow(detail).ShowDialog(this);

    /// <summary>
    /// Modal, and sharing this window's view model, so the Tools and Updates sections drive the same
    /// commands the rest of the app does rather than a second copy of them.
    /// </summary>
    private Task ShowSettingsAsync() =>
        new SettingsWindow { DataContext = DataContext }.ShowDialog(this);

    /// <summary>
    /// Relaunches the app so a colour change takes effect. Started before shutting down, because the
    /// new process has to exist before this one stops holding the window.
    /// </summary>
    private void Restart()
    {
        // Null only for a single-file host that has been stripped; there is nothing to relaunch then.
        if (Environment.ProcessPath is not { } exe)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Could not relaunch, so do not close the one window the user still has.
            return;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            Close();
        }
    }

    /// <summary>
    /// The Save As dialog, shared by the Script tab and the Diagrams tab's five export formats.
    /// </summary>
    /// <remarks>
    /// The file type comes from the suggested name's extension rather than from a parameter. Every
    /// caller already has to decide the extension to build the name, and asking for it twice is how
    /// the two end up disagreeing.
    /// </remarks>
    private async Task<string?> PickSaveFileAsync(string suggestedName, string? startFolder)
    {
        var extension = System.IO.Path.GetExtension(suggestedName).TrimStart('.').ToLowerInvariant();
        var label = extension switch
        {
            "sql" => "SQL script",
            "json" => "JSON file",
            "svg" => "SVG image",
            "png" => "PNG image",
            "pdf" => "PDF document",
            "mmd" => "Mermaid diagram",
            _ => "File",
        };

        var options = new FilePickerSaveOptions
        {
            Title = "Save " + label.ToLowerInvariant(),
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            // The OS dialog does its own overwrite prompt, which is why the app only asks when
            // writing straight into a configured scripts folder.
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(label) { Patterns = ["*." + extension] }],
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
