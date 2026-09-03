using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using EfAssist.App.ViewModels;
using EfAssist.Core;

namespace EfAssist.App.Views;

/// <summary>
/// The settings screen: theme and colours, text, code and console, the defaults a new workspace
/// starts from, diagrams, dotnet-ef, the shortcut reference, and updates. Modal, and shares the
/// shell's view model rather than owning its own, so every row drives exactly the same commands and
/// properties the rest of the app does.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>
    /// Each category's pane, so the search can filter the rows inside one and count what is left.
    /// Built once: the panes are all in the tree, hidden rather than created on demand.
    /// </summary>
    private readonly Dictionary<SettingsCategory, Control> _panes;

    private SettingsViewModel? _settings;

    public SettingsWindow()
    {
        InitializeComponent();

        _panes = new Dictionary<SettingsCategory, Control>
        {
            [SettingsCategory.Theme] = ThemePane,
            [SettingsCategory.TextAndLayout] = TextPane,
            [SettingsCategory.CodeAndConsole] = CodePane,
            [SettingsCategory.WorkspaceDefaults] = WorkspacePane,
            [SettingsCategory.Diagrams] = DiagramPane,
            [SettingsCategory.Tools] = ToolsPane,
            [SettingsCategory.Shortcuts] = ShortcutsPane,
            [SettingsCategory.About] = AboutPane,
        };

        // Everything here applies as it is changed, so Close is the only action.
        CloseButton.Click += (_, _) => Close();
        Closing += OnClosing;

        // The view owns the storage provider and the modal windows the About rows need, so it
        // supplies those rather than the view model reaching for UI services.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }

            if (_settings is not null)
            {
                _settings.PropertyChanged -= OnSettingsPropertyChanged;
            }

            _settings = viewModel.Appearance;
            _settings.PropertyChanged += OnSettingsPropertyChanged;

            _settings.PickFolderAsync = PickScriptFolderAsync;
            _settings.PickExportFileAsync = PickExportFileAsync;
            _settings.PickImportFileAsync = PickImportFileAsync;
            _settings.RevealFileAsync = path => MainWindow.OpenWithShellAsync(path, reveal: true);
            _settings.ConfirmAsync = request => new ConfirmWindow(request).ShowDialog<bool>(this);

            RestoreWindowLayout(_settings.WindowLayout);
            ApplySearch(_settings);
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Typing is the fastest way to the row you came for, and it costs a keyboard user nothing:
        // Tab still reaches the category list, and Escape still closes the window.
        SearchBox.Focus();
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is SettingsViewModel settings
            && e.PropertyName is nameof(SettingsViewModel.Query) or nameof(SettingsViewModel.Category))
        {
            ApplySearch(settings);
        }
    }

    /// <summary>
    /// Hides the rows that do not match what is typed, marks which categories still have any, and
    /// moves the selection off a category that has none.
    /// </summary>
    /// <remarks>
    /// A category whose own name or blurb matches keeps all of its rows: someone searching "diagrams"
    /// wants the Diagrams pane, not an empty one. That is also why the count label only appears when
    /// rows were actually hidden.
    /// </remarks>
    private void ApplySearch(SettingsViewModel settings)
    {
        var query = settings.Query.Trim();
        var searching = query.Length > 0;

        foreach (var section in settings.Sections)
        {
            if (!_panes.TryGetValue(section.Category, out var pane))
            {
                continue;
            }

            var (visible, total) = SettingsSearch.Apply(pane, query);
            var categoryMatches = Contains(section.Title, query)
                || Contains(section.Blurb, query)
                || Contains(section.Keywords, query);

            if (searching && visible == 0 && categoryMatches)
            {
                SettingsSearch.ShowAll(pane);
                visible = total;
            }

            section.Matches = !searching || visible > 0 || categoryMatches;
            section.MatchCount = visible;
            // Theme and Shortcuts are galleries rather than rows of settings, so they have nothing to
            // count: a "0" badge beside a category the search deliberately kept would read as a lie.
            section.ShowMatchCount = searching && section.Matches && total > 0;
            section.CountLabel = searching && visible < total ? $"{visible} of {total} shown" : "";
        }

        // Landing on a hidden pane would look like a screen with nothing on it.
        if (settings.Sections.FirstOrDefault(s => s.Category == settings.Category) is { Matches: false })
        {
            if (settings.Sections.FirstOrDefault(s => s.Matches) is { } first)
            {
                settings.Category = first.Category;
            }
        }
    }

    private static bool Contains(string haystack, string query) =>
        query.Length == 0 || haystack.Contains(query, StringComparison.OrdinalIgnoreCase);

    private async Task<string?> PickScriptFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Folder for generated scripts",
            AllowMultiple = false,
        });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickExportFileAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export settings",
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            FileTypeChoices = [JsonFiles],
        });

        return file?.TryGetLocalPath();
    }

    private async Task<string?> PickImportFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import settings",
            AllowMultiple = false,
            FileTypeFilter = [JsonFiles],
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private static FilePickerFileType JsonFiles => new("Settings file")
    {
        Patterns = ["*.json"],
    };

    /// <summary>
    /// Restores the size the window was last left at, and nothing else.
    /// </summary>
    /// <remarks>
    /// Deliberately not the position: this is a modal over the shell, and on a multi-monitor desk a
    /// remembered position opens it on whichever screen it was last dragged to, away from the window
    /// it belongs to. Centre-on-owner is right every time, which is not true of the main window —
    /// that one is the application, and where the user parked it is the point.
    /// </remarks>
    private void RestoreWindowLayout(WindowSettings layout)
    {
        if (layout is { Width: > 0, Height: > 0 })
        {
            Width = layout.Width.Value;
            Height = layout.Height.Value;
        }

        if (layout.Maximised)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_settings is null || WindowState == WindowState.Minimized)
        {
            return;
        }

        _settings.SaveWindowLayout(
            maximised: WindowState == WindowState.Maximized,
            size: WindowState == WindowState.Normal
                ? (ClientSize.Width, ClientSize.Height)
                : null);
    }
}
