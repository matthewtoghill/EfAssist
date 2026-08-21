using System.Text;

namespace EfAssist.Core;

/// <summary>The outcome of one <c>dotnet ef</c> invocation.</summary>
public sealed record EfResult(
    int ExitCode,
    IReadOnlyList<OutputLine> Lines,
    string CommandLine,
    string WorkingDirectory)
{
    public bool Success => ExitCode == 0;

    /// <summary>The <c>data:</c> lines joined back together — the JSON or SQL payload.</summary>
    public string Data => Join(OutputChannel.Data);

    /// <summary>
    /// The human-readable failure message. EF puts this on the <c>error:</c> line; any stack trace
    /// arrives on the <c>info:</c> lines before it, which is why those are excluded here.
    /// Falls back to unprefixed output, which is where MSBuild failures land.
    /// </summary>
    public string ErrorMessage
    {
        get
        {
            var errors = Join(OutputChannel.Error);
            if (errors.Length > 0)
            {
                return errors;
            }

            var raw = Lines.Where(l => l.Channel == OutputChannel.Raw && l.Text.Trim().Length > 0);
            return string.Join(Environment.NewLine, raw.Select(l => l.Text));
        }
    }

    /// <summary>Everything, unfiltered, for the "Copy diagnostics" button.</summary>
    public string Diagnostics
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Command:   {CommandLine}");
            sb.AppendLine($"Directory: {WorkingDirectory}");
            sb.AppendLine($"Exit code: {ExitCode}");
            sb.AppendLine();
            foreach (var line in Lines)
            {
                sb.AppendLine($"[{line.Channel}] {line.Text}");
            }

            return sb.ToString();
        }
    }

    private string Join(OutputChannel channel) => string.Join(
        Environment.NewLine,
        Lines.Where(l => l.Channel == channel).Select(l => l.Text));
}
