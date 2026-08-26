using System.Text.Json;
using System.Text.Json.Serialization;
using EfAssist.Core.Diagrams;

namespace EfAssist.Core;

/// <summary>
/// When to run context discovery. Discovery builds the startup project, so it is not free — and the
/// set of DbContext types in a solution rarely changes, which is why the default reuses the list
/// already known for a workspace.
/// </summary>
public enum DiscoveryMode
{
    /// <summary>
    /// Default, and first so that <c>default(DiscoveryMode)</c> matches it. Reuse the contexts
    /// remembered from last time and run nothing; discover only when there is no remembered list
    /// yet. Refresh re-runs discovery on demand.
    /// </summary>
    Cached,

    /// <summary>Discover as soon as the workspace opens. Cancellable.</summary>
    Auto,

    /// <summary>Never discover automatically, not even when nothing is remembered. Wait for Refresh.</summary>
    Manual,

    /// <summary>
    /// Try with <c>--no-build</c> first — near-instant when the solution is already built — and
    /// retry with a build only if that fails.
    /// </summary>
    AutoNoBuildFirst,
}

/// <summary>Per-workspace choices, remembered so the user picks projects once, not every session.</summary>
public sealed class WorkspaceSettings
{
    public string? StartupProject { get; set; }
    public string? MigrationsProject { get; set; }
    public string? Context { get; set; }

    /// <summary>Per workspace on purpose: a small solution can afford Auto, a huge one wants Manual.</summary>
    public DiscoveryMode Discovery { get; set; } = DiscoveryMode.Cached;

    /// <summary>
    /// When to load the migrations list. Same choices as <see cref="Discovery"/>, because the
    /// trade-off is the same one: the list costs a build, and sometimes a database round trip.
    /// </summary>
    public DiscoveryMode MigrationRefresh { get; set; } = DiscoveryMode.Cached;

    /// <summary>
    /// The migrations found last time, so the list can be shown without building. Applied state is
    /// deliberately never stored — see <see cref="MigrationInfo.Applied"/>. A remembered row always
    /// reads as Unknown until it is refreshed, because a stale "applied" is worse than no answer.
    /// </summary>
    public List<MigrationInfo> KnownMigrations { get; set; } = [];

    /// <summary>Pass <c>--no-connect</c>: list migrations without reaching the database.</summary>
    public bool Offline { get; set; }

    /// <summary>
    /// Generate idempotent scripts, checking the migrations history table. Shared by the Script tab
    /// and the migration detail pane's SQL preview, so it lives once, per workspace, alongside
    /// <see cref="NoBuild"/> and <see cref="Offline"/> rather than on either view.
    /// </summary>
    public bool Idempotent { get; set; }

    /// <summary>
    /// The contexts found last time discovery ran here, so the dropdown can be populated without
    /// building anything. Refreshed whenever discovery succeeds.
    /// </summary>
    public List<DbContextRef> KnownContexts { get; set; } = [];

    /// <summary>Where <c>migrations script</c> writes. Null means prompt with a Save As dialog.</summary>
    public string? ScriptOutputFolder { get; set; }

    /// <summary>Last folder used in a Save As dialog, so the next one opens somewhere useful.</summary>
    public string? LastSaveAsFolder { get; set; }

    public bool NoBuild { get; set; }

    /// <summary>
    /// Which diagram view this workspace was last left on. Null means never chosen here, so the
    /// app-wide <see cref="DisplaySettings.DefaultDiagramKind"/> decides.
    /// </summary>
    public DiagramKind? DiagramView { get; set; }

    /// <summary>
    /// Which way the diagram's ranks run. One setting for the tab rather than one per view: dragged
    /// positions are kept per view and orientation regardless, so nothing overlaps either way.
    /// </summary>
    public DiagramFlow DiagramLayoutFlow { get; set; }

    /// <summary>
    /// The diagram's display toggles. Per workspace rather than app-wide: how much of a model is
    /// worth showing at once depends on the model.
    /// </summary>
    public DiagramViewOptions? DiagramOptions { get; set; }

    /// <summary>
    /// Whether node dragging is unlocked. Locked by default, and remembered per workspace — a
    /// hand-arranged diagram wants to stay that way between sessions.
    /// </summary>
    public bool DiagramLocked { get; set; } = true;

    /// <summary>
    /// Last folder a diagram was exported to.
    /// </summary>
    /// <remarks>
    /// Its own field rather than sharing <see cref="LastSaveAsFolder"/> with the Script tab. Both tabs
    /// write their settings on every persist, so one field would mean whichever ran last wins — and
    /// a null from the tab that has not saved anything yet would quietly wipe the other's.
    /// </remarks>
    public string? DiagramSaveFolder { get; set; }
}

/// <summary>Which theme variant the UI uses.</summary>
public enum AppTheme
{
    /// <summary>Default, and first so that <c>default(AppTheme)</c> matches it: follow the OS setting.</summary>
    System,

    Light,

    Dark,
}

/// <summary>
/// The main window's remembered geometry. Written when the window closes rather than chosen on the
/// settings screen — only <see cref="Maximised"/> is offered there as a checkbox.
/// </summary>
public sealed class WindowSettings
{
    /// <summary>Open maximised. Also updated to match how the window was last closed.</summary>
    public bool Maximised { get; set; }

    /// <summary>
    /// The size of the restored — not maximised — window, in logical units. Null means nothing
    /// remembered yet, so the window opens at its designed size.
    /// </summary>
    public double? Width { get; set; }

    public double? Height { get; set; }

    /// <summary>
    /// Top-left of the restored window in physical pixels, which is what the platform deals in.
    /// Null, or a point on no connected screen, means the OS picks the position.
    /// </summary>
    public int? X { get; set; }

    public int? Y { get; set; }
}

/// <summary>Preferences that are the same wherever the app is pointed.</summary>
public sealed class DisplaySettings
{
    /// <summary>Wrap long console lines instead of scrolling horizontally.</summary>
    public bool WrapOutput { get; set; }

    /// <summary>
    /// Wrap long lines in the SQL viewer instead of scrolling horizontally. Off by default: EF's
    /// generated SQL has meaningful indentation that wrapping breaks up.
    /// </summary>
    public bool WrapSql { get; set; }

    /// <summary>
    /// Show the migrations list newest first. Off means EF's own chronological order, which is the
    /// order migrations are applied in.
    /// </summary>
    public bool SortNewestFirst { get; set; }

    /// <summary>
    /// Show line numbers in the migration source and SQL viewers. On by default, which is how the
    /// viewers behaved before this was a choice.
    /// </summary>
    public bool ShowLineNumbers { get; set; } = true;

    /// <summary>
    /// Show the Migrations tab's action panel expanded. On by default; collapsing it gives the
    /// height back to the migrations list and the detail pane.
    /// </summary>
    public bool MigrationActionsExpanded { get; set; } = true;

    /// <summary>
    /// Show the workspace settings panel down the left expanded. On by default; collapsing it folds
    /// the panel down to a rail and gives the width back to whichever tab is open.
    /// </summary>
    public bool LeftPanelExpanded { get; set; } = true;

    /// <summary>
    /// Show the output console expanded. On by default; collapsing it folds the console down to its
    /// header bar and gives the height back to whichever tab is open.
    /// </summary>
    public bool OutputExpanded { get; set; } = true;

    /// <summary>
    /// Show the Diagrams tab's view options expanded. Off by default: the surface wants the height,
    /// and the defaults are the useful ones.
    /// </summary>
    public bool DiagramOptionsExpanded { get; set; }

    /// <summary>
    /// Show the Diagrams tab's entity detail pane. On by default — it is where the metadata a node
    /// has no room for lives — but closing it gives the whole width back to the diagram.
    /// </summary>
    public bool DiagramDetailVisible { get; set; } = true;

    /// <summary>
    /// Which diagram a workspace opens on the first time it is looked at. App-wide, because it is a
    /// property of how the person thinks about their model rather than of the solution. A workspace
    /// that has been switched remembers its own choice in
    /// <see cref="WorkspaceSettings.DiagramView"/>.
    /// </summary>
    public DiagramKind DefaultDiagramKind { get; set; } = DiagramKind.EntityRelationship;

    /// <summary>
    /// Where the main window was last left, so it opens where it was closed rather than in the
    /// middle of whichever screen the OS picks.
    /// </summary>
    public WindowSettings Window
    {
        get => _window;
        set => _window = value ?? new WindowSettings();
    }

    private WindowSettings _window = new();

    /// <summary>
    /// Light, dark, or follow the OS. App-wide rather than per workspace: it is a property of the
    /// person looking at the screen, not of the solution they happen to have open.
    /// </summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>
    /// Which palette the colours start from. Independent of <see cref="Theme"/> — see
    /// <see cref="ThemePreset"/>.
    /// </summary>
    public ThemePreset Preset { get; set; } = ThemePreset.Default;

    /// <summary>
    /// Colour overrides for the light variant. Stored per variant rather than once, because a
    /// background that works on light is unreadable on dark and vice versa.
    /// </summary>
    public ThemeColours LightColours
    {
        get => _lightColours;
        // A settings file with an explicit null here would otherwise deserialise to one, and every
        // reader would need a guard. Absorb it once instead.
        set => _lightColours = value ?? new ThemeColours();
    }

    public ThemeColours DarkColours
    {
        get => _darkColours;
        set => _darkColours = value ?? new ThemeColours();
    }

    private ThemeColours _lightColours = new();
    private ThemeColours _darkColours = new();

    /// <summary>Base size for UI text, in points. Everything chrome-side scales from it.</summary>
    public double UiFontSize { get; set; } = ThemePresets.DefaultUiFontSize;

    /// <summary>
    /// Size for the monospace panes — the migration source, the SQL preview and the output console.
    /// Separate from <see cref="UiFontSize"/> so a large UI does not force a large code pane.
    /// </summary>
    public double EditorFontSize { get; set; } = ThemePresets.DefaultEditorFontSize;

    /// <summary>The overrides for one variant, so callers do not repeat the light/dark branch.</summary>
    public ThemeColours ColoursFor(bool dark) => dark ? DarkColours : LightColours;
}

public sealed class AppSettings
{
    /// <summary>
    /// Workspace settings already read from disk this session, keyed by the workspace's absolute
    /// path. Not serialised: each workspace lives in its own file under
    /// <c>workspaces/</c>, loaded on first use by <see cref="For"/>. The core file stays small and
    /// startup cost does not grow with the number of workspaces ever opened.
    /// </summary>
    [JsonIgnore]
    public Dictionary<string, WorkspaceSettings> Workspaces { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Folder holding the core settings file, and the <c>workspaces/</c> folder beside it. Set by
    /// <see cref="SettingsStore"/> on load and on save. Null means nothing has been given a
    /// location yet, so <see cref="For"/> has nowhere to read from and returns fresh defaults.
    /// </summary>
    [JsonIgnore]
    public string? Root { get; set; }

    /// <summary>Application-wide preferences, not tied to any one workspace.</summary>
    public DisplaySettings Display
    {
        get => _display;
        set => _display = value ?? new DisplaySettings();
    }

    private DisplaySettings _display = new();

    /// <summary>Most recent first.</summary>
    public List<string> RecentWorkspaces { get; set; } = [];

    /// <summary>
    /// The settings for a workspace, read from its own file the first time it is asked for and
    /// cached thereafter. Never null and never throws: an unknown or unreadable workspace gets
    /// defaults, same as a first run.
    /// </summary>
    public WorkspaceSettings For(string workspacePath)
    {
        var key = Path.GetFullPath(workspacePath);
        if (!Workspaces.TryGetValue(key, out var settings))
        {
            settings = SettingsStore.LoadWorkspace(Root, key) ?? new WorkspaceSettings();
            Workspaces[key] = settings;
        }

        return settings;
    }

    public void MarkRecent(string workspacePath, int keep = 10)
    {
        var key = Path.GetFullPath(workspacePath);
        RecentWorkspaces.RemoveAll(p => string.Equals(p, key, StringComparison.OrdinalIgnoreCase));
        RecentWorkspaces.Insert(0, key);
        if (RecentWorkspaces.Count > keep)
        {
            RecentWorkspaces.RemoveRange(keep, RecentWorkspaces.Count - keep);
        }
    }

    /// <summary>Drops a workspace from the recent list only. Its settings file, if any, is untouched.</summary>
    public void RemoveRecent(string workspacePath)
    {
        var key = Path.GetFullPath(workspacePath);
        RecentWorkspaces.RemoveAll(p => string.Equals(p, key, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Loads and saves settings as JSON under the user's app data folder: one core file for
/// app-wide preferences and the recent list, plus a file per workspace under <c>workspaces/</c>.
/// The split keeps the core file small — a single workspace's remembered migration list is
/// already larger than everything else put together.
/// </summary>
public static class SettingsStore
{
    /// <summary>
    /// Shared by everything the app writes as JSON, including <see cref="Diagrams.DiagramStore"/>,
    /// so a settings file and a saved diagram never disagree about how an enum is written.
    /// </summary>
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Enums as names, so the settings file stays readable and reordering the enum can't
        // silently change what a saved value means.
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EfAssist",
        "settings.json");

    /// <summary>
    /// Never throws. A missing file is a first run. A corrupt file is set aside as
    /// <c>.corrupt</c> rather than silently overwritten, so a user who cares can recover it.
    /// Workspace files are not read here — see <see cref="AppSettings.For"/>.
    /// </summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        var root = Path.GetDirectoryName(Path.GetFullPath(path));

        var settings = ReadOrDefault<AppSettings>(path) ?? new AppSettings();
        settings.Root = root;

        // A hand-edited file is the realistic source of a 400pt font, and rendering over it is not
        // worth it. Clamp once, here, so nothing downstream has to. Null blocks are handled by the
        // setters on DisplaySettings itself.
        settings.Display.UiFontSize = ThemePresets.ClampFontSize(
            settings.Display.UiFontSize, ThemePresets.DefaultUiFontSize);
        settings.Display.EditorFontSize = ThemePresets.ClampFontSize(
            settings.Display.EditorFontSize, ThemePresets.DefaultEditorFontSize);

        return settings;
    }

    /// <summary>
    /// Writes the core file and every workspace file loaded this session. Only workspaces that
    /// were actually opened are in the cache, so this is normally one or two small files.
    /// </summary>
    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        // A settings file always sits in a directory; the null case is a bare drive root.
        var root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        settings.Root = root;

        WriteAtomic(path, settings);

        foreach (var (workspacePath, workspace) in settings.Workspaces)
        {
            WriteAtomic(WorkspaceFile(root, workspacePath), workspace);
        }
    }

    /// <summary>
    /// Reads one workspace's file. Returns null when there is no file, no root, or the file is
    /// unreadable — all of which mean "no remembered choices", not an error.
    /// </summary>
    internal static WorkspaceSettings? LoadWorkspace(string? root, string workspacePath) =>
        root is null ? null : ReadOrDefault<WorkspaceSettings>(WorkspaceFile(root, workspacePath));

    /// <summary>
    /// A workspace's file name: the solution or folder name for a human reading the directory,
    /// plus a hash of the full path so that two <c>Api.slnx</c> files in different repos, or paths
    /// differing only in case, never collide.
    /// </summary>
    public static string WorkspaceFile(string root, string workspacePath) =>
        Path.Combine(root, "workspaces", WorkspaceKey(workspacePath) + ".json");

    /// <summary>
    /// A filename-safe identity for a workspace: its solution or folder name, so a human reading the
    /// directory can tell what is what, plus a hash of the full path so that two <c>Api.slnx</c>
    /// files in different repositories, or paths differing only in case, never collide.
    /// </summary>
    /// <remarks>
    /// Public because saved diagrams are filed under the same key —
    /// <c>diagrams/&lt;key&gt;/&lt;context&gt;.json</c> beside <c>workspaces/&lt;key&gt;.json</c>. One
    /// hashing rule for both, rather than two that could disagree about the same workspace.
    /// </remarks>
    public static string WorkspaceKey(string workspacePath)
    {
        var full = Path.GetFullPath(workspacePath);
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..8];

        var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar));
        var slug = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (slug.Length > 40)
        {
            slug = slug[..40];
        }

        return $"{slug}-{hash}";
    }

    /// <summary>
    /// Reads one JSON file. Never throws: a missing file, an unreadable one and a corrupt one all mean
    /// "nothing remembered". A corrupt file is set aside as <c>.corrupt</c> rather than overwritten.
    /// </summary>
    internal static T? ReadOrDefault<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (JsonException)
        {
            TryPreserveCorrupt(path);
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes to a temp file and moves it into place, so a crash or a full disk part-way through
    /// leaves the previous settings intact rather than a truncated file.
    /// </summary>
    internal static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, overwrite: true);
    }

    private static void TryPreserveCorrupt(string path)
    {
        try
        {
            File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch (IOException)
        {
            // Nothing useful to do; the caller falls back to defaults either way.
        }
    }
}
