namespace EfMigrateHub.Core;

/// <summary>Which stream a line of <c>dotnet ef</c> output belongs to, after the
/// <c>--prefix-output</c> prefix has been stripped.</summary>
public enum OutputChannel
{
    /// <summary>No recognised prefix. MSBuild writes its own warnings and errors this way,
    /// so this is a normal case, not a parse failure.</summary>
    Raw,
    Info,
    Data,
    Warn,
    Error,
}

/// <summary>One line of <c>dotnet ef</c> output with its channel identified.</summary>
public readonly record struct OutputLine(OutputChannel Channel, string Text)
{
    /// <summary>
    /// Width of the <c>--prefix-output</c> prefix field. EF pads the prefix into a fixed
    /// left-aligned column ("info:" + 4 spaces, "error:" + 3 spaces, ...), so the payload
    /// always starts at this index.
    /// </summary>
    private const int PrefixWidth = 9;

    private static readonly (string Token, OutputChannel Channel)[] Prefixes =
    [
        ("info:", OutputChannel.Info),
        ("data:", OutputChannel.Data),
        ("warn:", OutputChannel.Warn),
        ("error:", OutputChannel.Error),
    ];

    /// <summary>
    /// Splits a raw output line into channel and payload. Payload is taken by fixed-width slice,
    /// never trimmed: generated SQL carries its own indentation immediately after the prefix
    /// field, and a blank SQL line arrives as exactly "data:    ".
    /// </summary>
    public static OutputLine Parse(string line)
    {
        foreach (var (token, channel) in Prefixes)
        {
            if (!line.StartsWith(token, StringComparison.Ordinal))
            {
                continue;
            }

            // Everything between the token and PrefixWidth must be padding, otherwise this is
            // ordinary text that happens to start with the token.
            if (line.Length >= PrefixWidth &&
                line.AsSpan(token.Length, PrefixWidth - token.Length).IsWhiteSpace())
            {
                return new OutputLine(channel, line[PrefixWidth..]);
            }

            // Prefix with nothing after it at all.
            if (line.Length == token.Length)
            {
                return new OutputLine(channel, "");
            }
        }

        return new OutputLine(OutputChannel.Raw, line);
    }
}
