using System.Text;

namespace EfAssist.Core;

/// <summary>
/// Builds the suggested filename for a generated SQL script. Stable for a given set of choices, so
/// regenerating the same range proposes the same name — and the user can edit it before it is used.
/// </summary>
public static class ScriptFileName
{
    /// <summary>Long enough for real migration names, short enough to stay well inside MAX_PATH.</summary>
    private const int MaxLength = 120;

    /// <param name="from">Null means from the beginning, which EF writes as migration "0".</param>
    /// <param name="to">Null means the latest migration.</param>
    public static string Suggest(string? context, string? from, string? to, bool idempotent)
    {
        var name = new StringBuilder();
        name.Append(Sanitise(string.IsNullOrWhiteSpace(context) ? "script" : context));
        name.Append('_');
        name.Append(Sanitise(string.IsNullOrWhiteSpace(from) ? "0" : from));
        name.Append("-to-");
        name.Append(Sanitise(string.IsNullOrWhiteSpace(to) ? "latest" : to));

        if (idempotent)
        {
            name.Append("_idempotent");
        }

        if (name.Length > MaxLength)
        {
            name.Length = MaxLength;
        }

        return name.Append(".sql").ToString();
    }

    /// <summary>
    /// Migration and context names are C# identifiers so they are already safe, but the value can
    /// also come from a hand-typed custom range, and a stray separator would silently write the
    /// script to a different directory.
    /// </summary>
    private static string Sanitise(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            cleaned.Append(invalid.Contains(character) ? '_' : character);
        }

        return cleaned.ToString().Trim().Trim('.');
    }
}
