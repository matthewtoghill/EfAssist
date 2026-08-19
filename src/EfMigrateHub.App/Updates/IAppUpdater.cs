using System.Threading.Tasks;

namespace EfMigrateHub.App.Updates;

/// <summary>
/// The one seam over Velopack. Tests need to fake "an update is available" without a GitHub release
/// and without an installed application, and every Velopack type stays behind this line.
/// </summary>
public interface IAppUpdater
{
    /// <summary>The running version, for display. Never null — falls back to the assembly version.</summary>
    string CurrentVersion { get; }

    /// <summary>
    /// False for a development run, a `dotnet run`, or the portable build: there is no install
    /// directory to update in place, so checking would only produce an error the user cannot act on.
    /// </summary>
    bool CanUpdate { get; }

    /// <summary>The new version, or null when already up to date. Throws if the check fails.</summary>
    Task<string?> CheckAsync();

    /// <summary>
    /// Downloads the update found by the last <see cref="CheckAsync"/> and restarts into it. Does
    /// nothing when no update is pending.
    /// </summary>
    Task ApplyAndRestartAsync();
}
