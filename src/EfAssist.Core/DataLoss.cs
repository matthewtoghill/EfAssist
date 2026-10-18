using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfAssist.Core;

/// <summary>
/// Finds the operations in a migration that may lose data — what <c>migrations add</c> means by
/// "An operation was scaffolded that may result in the loss of data", which it prints once and
/// never says again.
/// </summary>
/// <remarks>
/// <para>
/// A syntax-only Roslyn parse of the migration's own <c>.cs</c> file, like
/// <see cref="Diagrams.ModelSnapshotParser"/>: nothing needs to build. Reads one method — Up for
/// what applying does, Down for what rolling back does — because the two run different code.
/// </para>
/// <para>
/// ponytail: matches the scaffolded call shapes only. Raw <c>migrationBuilder.Sql("DROP …")</c> and
/// operations built in helper methods are invisible, so an empty result means "nothing found",
/// never "safe". Good enough for EF's own output, which is what raised the warning.
/// </para>
/// </remarks>
public static class DataLoss
{
    /// <param name="source">The migration file's contents.</param>
    /// <param name="method"><c>"Up"</c> or <c>"Down"</c>.</param>
    /// <returns>One description per operation, in source order, such as <c>drops column Blogs.Url</c>.</returns>
    public static IReadOnlyList<string> Scan(string source, string method)
    {
        ArgumentNullException.ThrowIfNull(source);

        var body = CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == method);

        if (body is null)
        {
            return [];
        }

        var found = new List<string>();
        foreach (var call in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax access)
            {
                continue;
            }

            var args = Arguments(call);
            var description = access.Name.Identifier.Text switch
            {
                "DropTable" => $"drops table {Table(args, "name")}",
                "DropColumn" => $"drops column {Table(args, "table")}.{args.GetValueOrDefault("name")}",
                "RenameColumn" =>
                    $"renames column {Table(args, "table")}.{args.GetValueOrDefault("name")} to {args.GetValueOrDefault("newName")}",
                "AlterColumn" => Narrowing(access.Name, args) is { Length: > 0 } why
                    ? $"alters column {Table(args, "table")}.{args.GetValueOrDefault("name")} ({why})"
                    : null,
                _ => null,
            };

            if (description is not null)
            {
                found.Add(description);
            }
        }

        return found;
    }

    /// <summary>
    /// EF's own rule for flagging an AlterColumn, so this agrees with the console line: the store
    /// type changed, or a nullable column became required. A shrinking length, precision or scale is
    /// added on top, because EF only catches that through the type string — a provider that
    /// scaffolds the length separately would otherwise slip through.
    /// </summary>
    private static string Narrowing(SimpleNameSyntax name, Dictionary<string, string> args)
    {
        var reasons = new List<string>();

        if (args.TryGetValue("type", out var type) && args.TryGetValue("oldType", out var oldType)
            && !string.Equals(type, oldType, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add($"{oldType} to {type}");
        }
        else if (name is GenericNameSyntax generic && args.TryGetValue("oldClrType", out var oldClr)
                 && generic.TypeArgumentList.Arguments.ToString() != oldClr)
        {
            reasons.Add($"{oldClr} to {generic.TypeArgumentList.Arguments}");
        }

        foreach (var limit in (string[])["maxLength", "precision", "scale"])
        {
            var old = "old" + char.ToUpperInvariant(limit[0]) + limit[1..];
            if (args.TryGetValue(limit, out var now) && int.TryParse(now, out var n)
                && (!args.TryGetValue(old, out var was) || (int.TryParse(was, out var w) && n < w)))
            {
                reasons.Add($"{limit} {(was is null ? "set to" : $"{was} to")} {n}");
            }
        }

        // Both default to false in AlterColumn's signature, so an absent argument means required.
        if (args.GetValueOrDefault("oldNullable") == "true" && args.GetValueOrDefault("nullable") != "true")
        {
            reasons.Add("made required");
        }

        return string.Join(", ", reasons);
    }

    private static string? Table(Dictionary<string, string> args, string key) =>
        args.TryGetValue("schema", out var schema) ? $"{schema}.{args.GetValueOrDefault(key)}" : args.GetValueOrDefault(key);

    /// <summary>Named arguments only — EF always scaffolds them named. Literal values unquoted.</summary>
    private static Dictionary<string, string> Arguments(InvocationExpressionSyntax call) =>
        call.ArgumentList.Arguments
            .Where(a => a.NameColon is not null)
            .GroupBy(a => a.NameColon!.Name.Identifier.Text)
            .ToDictionary(
                g => g.Key,
                g => g.First().Expression switch
                {
                    LiteralExpressionSyntax literal => literal.Token.ValueText,
                    TypeOfExpressionSyntax typeOf => typeOf.Type.ToString(),
                    var other => other.ToString(),
                });
}
