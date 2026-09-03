using CommunityToolkit.Mvvm.ComponentModel;

namespace EfAssist.App.ViewModels;

/// <summary>Which pane of the settings screen is showing.</summary>
public enum SettingsCategory
{
    /// <summary>Default, and first so <c>default(SettingsCategory)</c> matches it.</summary>
    Theme,

    TextAndLayout,

    CodeAndConsole,

    WorkspaceDefaults,

    Diagrams,

    Tools,

    Shortcuts,

    About,
}

/// <summary>
/// One entry in the settings screen's category list, and the heading of the pane it selects.
/// </summary>
/// <remarks>
/// <see cref="Matches"/> and <see cref="CountLabel"/> are written by the search, which runs in the
/// view: the rows are declared in XAML, so the view is the only thing that can count them. See
/// <c>SettingsSearch</c>.
/// </remarks>
public sealed partial class SettingsSection(
    SettingsCategory category,
    string title,
    string blurb,
    string keywords = "") : ObservableObject
{
    public SettingsCategory Category { get; } = category;

    public string Title { get; } = title;

    /// <summary>The line under the pane's heading. Searched, as well as shown.</summary>
    public string Blurb { get; } = blurb;

    /// <summary>
    /// Extra search terms for the panes whose contents are not a list of labelled rows — the palette
    /// names on Theme, the gestures on Shortcuts — so searching for "nord" still finds Theme.
    /// </summary>
    public string Keywords { get; } = keywords;

    /// <summary>False while a search is running and nothing in this category matches it.</summary>
    [ObservableProperty]
    private bool _matches = true;

    /// <summary>
    /// "3 of 6 shown" while a search is narrowing this pane, empty otherwise. Shown beside the
    /// pane's heading, so it is obvious that rows are missing rather than gone.
    /// </summary>
    [ObservableProperty]
    private string _countLabel = "";

    /// <summary>The number of matching rows, for the badge on the category list.</summary>
    [ObservableProperty]
    private int _matchCount;

    /// <summary>True only while a search is running, so the badge stays hidden the rest of the time.</summary>
    [ObservableProperty]
    private bool _showMatchCount;
}
