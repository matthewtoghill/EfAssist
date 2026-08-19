using System.Diagnostics;
using System.Text;

namespace EfMigrateHub.Core;

/// <summary>
/// Runs the <c>dotnet</c> CLI and classifies its output. The one seam in Core worth an interface:
/// tests need to supply canned output without launching a process.
/// </summary>
public interface IEfRunner
{
    /// <param name="args">
    /// Arguments passed to <c>dotnet</c> verbatim. Usually an <see cref="EfArgs"/> list starting
    /// with "ef", but <see cref="Workspace"/> reuses this for <c>dotnet sln list</c>.
    /// </param>
    /// <param name="workingDirectory">
    /// Where to launch from. Note that <c>dotnet ef</c> resets its own working directory to the
    /// target project's folder, so this does not affect how relative connection strings resolve.
    /// </param>
    /// <param name="progress">Receives each line as it arrives, for live console output.</param>
    Task<EfResult> RunAsync(
        IReadOnlyList<string> args,
        string workingDirectory,
        IProgress<OutputLine>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class EfRunner : IEfRunner
{
    private const string AspNetCoreEnvironment = "ASPNETCORE_ENVIRONMENT";
    private const string DotNetEnvironment = "DOTNET_ENVIRONMENT";
    private const string DevelopmentEnvironment = "Development";

    /// <summary>
    /// The environment name design-time commands run under: whatever the app inherited, or
    /// <c>Development</c>. Reported in diagnostics because it decides whether the startup project's
    /// user secrets are read at all.
    /// </summary>
    public static string HostEnvironment =>
        Environment.GetEnvironmentVariable(AspNetCoreEnvironment)
        ?? Environment.GetEnvironmentVariable(DotNetEnvironment)
        ?? DevelopmentEnvironment;

    public async Task<EfResult> RunAsync(
        IReadOnlyList<string> args,
        string workingDirectory,
        IProgress<OutputLine>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // Keep EF's messages in English so the Phase 5 error mapping has something stable to match.
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        // `dotnet ef` builds and runs the startup project's design-time host, and that host decides
        // for itself whether to layer user secrets over appsettings.json: both WebApplicationBuilder
        // and HostApplicationBuilder call AddUserSecrets only when the environment is Development.
        // Nothing sets that here — launchSettings.json is an IDE/`dotnet run` file that the EF tools
        // never read, and a GUI process launched from Explorer inherits no environment variable — so
        // without this the host comes up as Production and silently uses the appsettings connection
        // string instead of the one in user secrets. Only filled in when neither variable is already
        // set, so launching the app from a shell that sets one still wins.
        if (!startInfo.Environment.ContainsKey(AspNetCoreEnvironment) &&
            !startInfo.Environment.ContainsKey(DotNetEnvironment))
        {
            startInfo.Environment[AspNetCoreEnvironment] = DevelopmentEnvironment;
            startInfo.Environment[DotNetEnvironment] = DevelopmentEnvironment;
        }

        var lines = new List<OutputLine>();
        var lock_ = new object();

        void Collect(string? data)
        {
            if (data is null)
            {
                return;
            }

            var line = OutputLine.Parse(data);

            // ponytail: one lock over the whole list. Output volume is a few hundred lines per
            // command; a channel or concurrent queue would be more machinery for no gain.
            lock (lock_)
            {
                lines.Add(line);
            }

            progress?.Report(line);
        }

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => Collect(e.Data);
        process.ErrorDataReceived += (_, e) => Collect(e.Data);

        var commandLine = "dotnet " + string.Join(' ', args.Select(Quote));

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            // Almost always "dotnet is not on PATH". Surface it as a normal failed result so the
            // caller's error handling and diagnostics work the same way as any other failure.
            return new EfResult(
                -1,
                [new OutputLine(OutputChannel.Error, ex.Message)],
                commandLine,
                workingDirectory);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Nothing should ever prompt us — database drop is invoked with --force — but an
        // unexpected prompt would otherwise block forever waiting on a stdin nobody writes to.
        process.StandardInput.Close();

        await using var kill = cancellationToken.Register(static state =>
        {
            try
            {
                var p = (Process)state!;
                if (!p.HasExited)
                {
                    // MSBuild spawns node processes; killing only the parent orphans them.
                    p.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited between the check and the kill.
            }
        }, process);

        // Deliberately not passing the token: we want to collect whatever output arrived before
        // the kill, then report cancellation ourselves.
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        lock (lock_)
        {
            return new EfResult(process.ExitCode, lines.ToArray(), commandLine, workingDirectory);
        }
    }

    private static string Quote(string arg) =>
        arg.Contains(' ', StringComparison.Ordinal) ? $"\"{arg}\"" : arg;
}
