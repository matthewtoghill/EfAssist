namespace EfAssist.Core;

/// <summary>
/// Builds the suggested filename for a migrations bundle. EF's own default is <c>efbundle</c> in the
/// working directory; this keeps that word, so the file is recognisable to anyone who knows the CLI,
/// and puts the context in front of it so two contexts' bundles do not collide in one folder.
/// </summary>
public static class BundleFileName
{
    /// <summary>Same ceiling as a script name: room for real context names, well inside MAX_PATH.</summary>
    private const int MaxLength = 120;

    /// <param name="targetRuntime">
    /// The RID the bundle is being built for, or null for the machine doing the bundling. This — not
    /// the host OS — decides the extension: a <c>linux-x64</c> bundle produced on Windows is still a
    /// Linux executable, and naming it <c>.exe</c> would be a lie the target machine has to live with.
    /// </param>
    public static string Suggest(string? context, string? targetRuntime)
    {
        var stem = ScriptFileName.Sanitise(
            string.IsNullOrWhiteSpace(context) ? "efbundle" : context + "-efbundle");

        if (stem.Length > MaxLength)
        {
            stem = stem[..MaxLength];
        }

        return stem + Extension(targetRuntime);
    }

    /// <summary>
    /// <c>.exe</c> for a Windows target, nothing for any other. An unrecognised RID is treated as
    /// non-Windows rather than guessed at: a missing extension is a rename, a wrong one is a file
    /// Explorer will happily try to run.
    /// </summary>
    private static string Extension(string? targetRuntime) =>
        string.IsNullOrWhiteSpace(targetRuntime)
            ? OperatingSystem.IsWindows() ? ".exe" : ""
            : targetRuntime.StartsWith("win", StringComparison.OrdinalIgnoreCase) ? ".exe" : "";
}
