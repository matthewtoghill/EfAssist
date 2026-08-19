using System.Text.Json;
using System.Text.Json.Serialization;

namespace EfMigrateHub.Core;

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
}

/// <summary>Which theme variant the UI uses.</summary>
public enum AppTheme
{
    /// <summary>Default, and first so that <c>default(AppTheme)</c> matches it: follow the OS setting.</summary>
    System,

    Light,

    Dark,
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
    /// Light, dark, or follow the OS. App-wide rather than per workspace: it is a property of the
    /// person looking at the screen, not of the solution they happen to have open.
    /// </summary>
    public AppTheme Theme { get; set; } = AppTheme.System;
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
    public DisplaySettings Display { get; set; } = new();

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
}

/// <summary>
/// Loads and saves settings as JSON under the user's app data folder: one core file for
/// app-wide preferences and the recent list, plus a file per workspace under <c>workspaces/</c>.
/// The split keeps the core file small — a single workspace's remembered migration list is
/// already larger than everything else put together.
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Enums as names, so the settings file stays readable and reordering the enum can't
        // silently change what a saved value means.
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EfMigrateHub",
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
    public static string WorkspaceFile(string root, string workspacePath)
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

        return Path.Combine(root, "workspaces", $"{slug}-{hash}.json");
    }

    private static T? ReadOrDefault<T>(string path) where T : class
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
    private static void WriteAtomic<T>(string path, T value)
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
