using System.Text.Json;

namespace EfMigrateHub.Core;

/// <summary>
/// Whether the tooling this app shells out to is actually present.
/// </summary>
/// <param name="Problem">Null when everything needed is available.</param>
public sealed record ToolStatus(
    bool EfToolAvailable,
    string? EfToolVersion,
    string? SdkVersion,
    string? Problem)
{
    public const string InstallCommand = "dotnet tool install --global dotnet-ef";
}

/// <summary>
/// Startup checks. Deliberately only checks that <c>dotnet ef</c> runs — no attempt to compare the
/// tool version against the project's EF Core version. EF already reports that mismatch itself with
/// a clear message ("The Entity Framework tools version ... is older than that of the runtime ..."),
/// and reproducing the check would mean either an unreliable file scan or another slow restore.
/// </summary>
public static class Preflight
{
    /// <param name="workingDirectory">
    /// Run this per workspace, not just once at startup: a local tool manifest
    /// (<c>.config/dotnet-tools.json</c>) makes <c>dotnet ef</c> availability directory-dependent.
    /// </param>
    public static async Task<ToolStatus> CheckAsync(
        IEfRunner runner,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var sdk = await runner.RunAsync(["--version"], workingDirectory, null, cancellationToken)
            .ConfigureAwait(false);
        var sdkVersion = sdk.Success ? LastMeaningfulLine(sdk) : null;

        // Neither of these builds anything, so this stays well under a second.
        var ef = await runner.RunAsync(["ef", "--version"], workingDirectory, null, cancellationToken)
            .ConfigureAwait(false);

        if (!ef.Success)
        {
            var problem = ef.ErrorMessage.Length > 0
                ? ef.ErrorMessage
                : "dotnet ef is not available. Install it, then reopen the workspace.";
            return new ToolStatus(false, null, sdkVersion, problem);
        }

        // Output is a title line followed by the version.
        return new ToolStatus(true, LastMeaningfulLine(ef), sdkVersion, null);
    }

    private static string? LastMeaningfulLine(EfResult result) => result.Lines
        .Select(l => l.Text.Trim())
        .LastOrDefault(t => t.Length > 0);

    /// <summary>
    /// Walks up from <paramref name="workingDirectory"/> looking for the nearest
    /// <c>.config/dotnet-tools.json</c> that pins <c>dotnet-ef</c> — the same manifest
    /// <c>dotnet tool</c> commands resolve against. Used to decide between a global and a local
    /// tool update, since running <c>dotnet tool update dotnet-ef</c> without <c>--global</c> fails
    /// outright when no manifest pins it here.
    /// </summary>
    public static bool HasLocalDotnetEfTool(string workingDirectory)
    {
        for (var dir = new DirectoryInfo(workingDirectory); dir is not null; dir = dir.Parent)
        {
            var manifestPath = Path.Combine(dir.FullName, ".config", "dotnet-tools.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                using var stream = File.OpenRead(manifestPath);
                using var manifest = JsonDocument.Parse(stream);
                return manifest.RootElement.TryGetProperty("tools", out var tools) &&
                    tools.TryGetProperty("dotnet-ef", out _);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return false;
    }
}
