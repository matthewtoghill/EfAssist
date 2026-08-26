using System.Text.Json;

namespace EfAssist.Core;

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
/// Startup checks: that <c>dotnet ef</c> runs, and what EF Core version the selected project
/// resolved to. The version comparison is informational only — EF itself still reports a genuine
/// blocking mismatch when a command runs ("The Entity Framework tools version ... is older than
/// that of the runtime ..."), and this is here so the two numbers can be seen side by side before
/// that happens.
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

    private const string EfCorePackage = "Microsoft.EntityFrameworkCore";

    /// <summary>
    /// The EF Core version a project actually resolved to, read from the restore output NuGet
    /// already wrote (<c>obj/project.assets.json</c>). Nothing is spawned and nothing is restored,
    /// and because the versions there are resolved rather than declared, this is also right for
    /// central package management, floating versions, and a transitive-only reference.
    /// </summary>
    /// <returns>
    /// Null when the project has never been restored, or resolved no EF Core at all. A caller
    /// should show nothing in that case rather than guessing.
    /// </returns>
    public static string? ProjectEfCoreVersion(string projectPath)
    {
        var directory = Path.GetDirectoryName(projectPath);
        if (directory is null)
        {
            return null;
        }

        var assetsPath = Path.Combine(directory, "obj", "project.assets.json");
        if (!File.Exists(assetsPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(assetsPath);
            using var assets = JsonDocument.Parse(stream);

            if (!assets.RootElement.TryGetProperty("libraries", out var libraries))
            {
                return null;
            }

            // Keys are "<package>/<resolved version>". Matching the exact package name rather than a
            // prefix keeps Abstractions, Relational and the providers out of it — they share their
            // version with the core package anyway, but only until someone pins one of them.
            return libraries.EnumerateObject()
                .Select(library => library.Name)
                .Where(name => name.StartsWith(EfCorePackage + "/", StringComparison.OrdinalIgnoreCase))
                .Select(name => name[(EfCorePackage.Length + 1)..])
                .FirstOrDefault(version => version.Length > 0);
        }
        catch (JsonException)
        {
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
    /// Whether <c>dotnet ef</c> is older than the EF Core the project resolved to — the direction
    /// that actually fails. Newer tools run older runtimes fine, so that case is not flagged.
    /// </summary>
    /// <remarks>
    /// ponytail: compares release numbers only — the prerelease label is dropped, so 10.0.0 and
    /// 10.0.0-rc.1 count as equal. Ordering prerelease labels properly needs NuGet's version rules,
    /// and getting it wrong here would mean a false warning on a preview SDK. Upgrade if someone
    /// running previews needs the two told apart.
    /// </remarks>
    public static bool ToolIsOlderThanProject(string? toolVersion, string? projectVersion) =>
        Release(toolVersion) is { } tool && Release(projectVersion) is { } project && tool < project;

    private static Version? Release(string? version)
    {
        if (version is null)
        {
            return null;
        }

        var release = version.Split('-', '+')[0].Trim();
        return Version.TryParse(release, out var parsed) ? parsed : null;
    }

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
