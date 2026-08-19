using System;
using System.Reflection;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace EfMigrateHub.App.Updates;

/// <summary>
/// Updates from the project's GitHub releases. Velopack owns the download, the delta patching and
/// the restart; this only holds the <see cref="UpdateInfo"/> between the check and the apply.
/// </summary>
public sealed class VelopackUpdater : IAppUpdater
{
    /// <summary>Where releases are published. `vpk upload github` writes to the same place.</summary>
    public const string RepositoryUrl = "https://github.com/matthewtoghill/EfMigrateHub";

    /// <summary>
    /// Built on first use, not in the constructor: <c>UpdateManager</c> throws unless
    /// <c>VelopackApp.Build().Run()</c> has run, which is true of the real app but not of the test
    /// host or the XAML previewer, both of which construct the shell view model directly. Null means
    /// "no updater here", which is exactly what <see cref="CanUpdate"/> should report.
    /// </summary>
    private readonly Lazy<UpdateManager?> _manager = new(() =>
    {
        try
        {
            return new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
        }
        catch
        {
            return null;
        }
    });

    private UpdateInfo? _pending;

    public string CurrentVersion =>
        _manager.Value?.CurrentVersion?.ToString()
        ?? Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "unknown";

    public bool CanUpdate => _manager.Value?.IsInstalled == true;

    public async Task<string?> CheckAsync()
    {
        if (_manager.Value is not { } manager)
        {
            return null;
        }

        _pending = await manager.CheckForUpdatesAsync();
        return _pending?.TargetFullRelease.Version.ToString();
    }

    public async Task ApplyAndRestartAsync()
    {
        if (_manager.Value is not { } manager || _pending is null)
        {
            return;
        }

        await manager.DownloadUpdatesAsync(_pending);
        manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }
}
