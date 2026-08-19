namespace EfMigrateHub.Core;

/// <summary>
/// Validates a migration name before it reaches <c>dotnet ef migrations add</c>. EF turns the name
/// into a C# class name, so an invalid one fails after a full build — worth catching in the UI first.
/// </summary>
public static class MigrationName
{
    /// <summary>
    /// A subset of C# keywords: only those a person might plausibly type as a migration name.
    /// ponytail: not the full keyword list, and not a Roslyn reference for one check. The cost of a
    /// miss is EF's own error message, which is already clear.
    /// </summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "base", "bool", "break", "byte", "case", "catch", "char", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
        "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if",
        "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new",
        "null", "object", "operator", "out", "override", "params", "private", "protected", "public",
        "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static",
        "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
        "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    /// <summary>
    /// Returns null when the name is usable, otherwise a message suitable for showing to the user.
    /// </summary>
    /// <param name="existing">
    /// Names already in the migrations list. EF permits duplicates and then fails to build, so
    /// rejecting them here is more useful than letting it through.
    /// </param>
    public static string? Validate(string? name, IEnumerable<string>? existing = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Enter a name for the migration.";
        }

        if (name != name.Trim())
        {
            return "The name cannot start or end with a space.";
        }

        if (!char.IsLetter(name[0]) && name[0] != '_')
        {
            return "The name must start with a letter or underscore.";
        }

        var invalid = name.FirstOrDefault(c => !char.IsLetterOrDigit(c) && c != '_');
        if (invalid != default)
        {
            return $"'{invalid}' is not allowed — use letters, digits and underscores only.";
        }

        if (Keywords.Contains(name))
        {
            return $"'{name}' is a C# keyword and cannot be used as a class name.";
        }

        if (existing is not null &&
            existing.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase)))
        {
            return $"A migration called '{name}' already exists.";
        }

        return null;
    }
}
