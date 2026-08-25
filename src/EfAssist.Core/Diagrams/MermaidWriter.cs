using System.Text;

namespace EfAssist.Core.Diagrams;

/// <summary>
/// Writes the diagram as Mermaid: <c>erDiagram</c> for the entity-relationship view,
/// <c>classDiagram</c> for the class view.
/// </summary>
/// <remarks>
/// <para>
/// Built from <see cref="DiagramNodeContent"/> rather than straight from the
/// <see cref="DiagramModel"/>, so the text describes the diagram actually on screen — the same
/// collapsed join tables, the same inlined owned types, the same property detail. A Mermaid export
/// that quietly ignored the view options would be a second, differently-shaped diagram.
/// </para>
/// <para>
/// Names are sanitised to letters, digits and underscores. Mermaid's own quoting rules differ between
/// its two diagram types and between versions; one conservative identifier form renders everywhere.
/// </para>
/// </remarks>
public static class MermaidWriter
{
    public static string Write(DiagramModel model, DiagramViewOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);

        var content = DiagramNodeContent.Build(model, options);

        return options.Kind == DiagramKind.Class
            ? ClassDiagram(content)
            : ErDiagram(content);
    }

    // ---- erDiagram ----

    private static string ErDiagram(DiagramNodeContent.Content content)
    {
        var names = Names(content);
        var text = new StringBuilder("erDiagram\n");

        foreach (var node in content.Nodes)
        {
            text.Append("    ").Append(names[node.EntityName]).Append(" {\n");

            foreach (var row in node.Rows.Where(r => r.Kind == RowKind.Property))
            {
                // Mermaid wants type then name, and both have to be single tokens — "nvarchar(450)"
                // and "decimal(18, 2)" are not.
                text.Append("        ")
                    .Append(Token(row.Type ?? "unknown"))
                    .Append(' ')
                    .Append(Token(row.Name));

                var key = row.IsKey ? "PK" : row.IsForeignKey ? "FK" : null;
                if (key is not null)
                {
                    text.Append(' ').Append(key);
                }

                text.Append('\n');
            }

            text.Append("    }\n");
        }

        foreach (var edge in content.Edges)
        {
            // Inheritance has no notation in an ER diagram — it is not a foreign key — and drawing it
            // as one would claim a join that does not exist. The class view is where it belongs.
            if (edge.Kind == EdgeKind.Inheritance)
            {
                continue;
            }

            // The edge runs dependent-to-principal; Mermaid reads its connector left-to-right, so the
            // principal goes first.
            text.Append("    ")
                .Append(names[edge.To])
                .Append(' ')
                .Append(Connector(edge))
                .Append(' ')
                .Append(names[edge.From])
                .Append(" : \"")
                .Append(EdgeLabel(edge))
                .Append("\"\n");
        }

        return text.ToString();
    }

    // ---- classDiagram ----

    private static string ClassDiagram(DiagramNodeContent.Content content)
    {
        var names = Names(content);
        var text = new StringBuilder("classDiagram\n");

        foreach (var node in content.Nodes)
        {
            text.Append("    class ").Append(names[node.EntityName]).Append(" {\n");

            foreach (var row in node.Rows.Where(r => r.Kind != RowKind.Index))
            {
                text.Append("        +");

                if (row.Type is { Length: > 0 } type)
                {
                    text.Append(Generic(type)).Append(' ');
                }

                text.Append(Token(row.Name)).Append('\n');
            }

            text.Append("    }\n");

            if (node.IsAbstractBase)
            {
                text.Append("    <<abstract>> ").Append(names[node.EntityName]).Append('\n');
            }
        }

        foreach (var edge in content.Edges)
        {
            var line = edge.Kind switch
            {
                // Base first: Mermaid's arrow points from the base to the derived type.
                EdgeKind.Inheritance =>
                    $"    {names[edge.To]} <|-- {names[edge.From]}",
                EdgeKind.Ownership =>
                    $"    {names[edge.To]} *-- {names[edge.From]}",
                EdgeKind.ManyToMany =>
                    $"    {names[edge.To]} \"*\" <--> \"*\" {names[edge.From]}",
                _ => $"    {names[edge.To]} \"{edge.ToLabel ?? "1"}\" --> \"{edge.FromLabel ?? "*"}\" {names[edge.From]}",
            };

            text.Append(line);

            if (edge.Kind is not EdgeKind.Inheritance && EdgeLabel(edge) is { Length: > 0 } label)
            {
                text.Append(" : ").Append(Token(label));
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    // ---- Names ----

    /// <summary>
    /// A Mermaid identifier per entity, keyed by the entity name the edges refer to.
    /// </summary>
    /// <remarks>
    /// Short names are what a reader wants, but two namespaces can hold the same short name, and two
    /// classes with one identifier would silently merge into one box. A collision falls back to the
    /// sanitised full name, which is ugly and correct.
    /// </remarks>
    private static Dictionary<string, string> Names(DiagramNodeContent.Content content)
    {
        var shortNames = content.Nodes
            .GroupBy(n => Token(Short(n.EntityName)), StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.First().EntityName, g => g.Key, StringComparer.Ordinal);

        return content.Nodes.ToDictionary(
            n => n.EntityName,
            n => shortNames.GetValueOrDefault(n.EntityName) ?? Token(n.EntityName),
            StringComparer.Ordinal);
    }

    private static string Short(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[(dot + 1)..];
    }

    /// <summary>
    /// The label Mermaid puts on the connector. <see cref="DiagramEdge.Label"/> is already the delete
    /// behaviour or nothing, depending on the view options, so there is nothing to re-decide here.
    /// </summary>
    private static string EdgeLabel(DiagramEdge edge) =>
        edge.Label is { Length: > 0 } label
            ? label
            : edge.Kind == EdgeKind.Ownership ? "owns" : "";

    /// <summary>
    /// The <c>||--o{</c> between two entities, read from the cardinalities the content builder already
    /// worked out rather than re-deriving them from the relationship.
    /// </summary>
    private static string Connector(DiagramEdge edge)
    {
        if (edge.Kind == EdgeKind.ManyToMany)
        {
            return "}o--o{";
        }

        // The principal is on the left, so its symbol reads outward from the line.
        var principal = edge.ToLabel == "0..1" ? "|o" : "||";
        var dependent = edge.FromLabel == "*" ? "o{" : "o|";
        return principal + "--" + dependent;
    }

    /// <summary>Letters, digits and underscores only, and never empty or starting with a digit.</summary>
    private static string Token(string text)
    {
        var token = string.Concat(text.Select(c => char.IsLetterOrDigit(c) ? c : '_')).Trim('_');
        return token.Length == 0 ? "_" : char.IsDigit(token[0]) ? "_" + token : token;
    }

    /// <summary>
    /// A CLR type as Mermaid writes generics: <c>ICollection~Post~</c>. Angle brackets are markup in
    /// a rendered Mermaid diagram and would swallow the type argument.
    /// </summary>
    private static string Generic(string type)
    {
        var open = type.IndexOf('<');
        if (open < 0 || !type.EndsWith('>'))
        {
            return Token(type);
        }

        return Token(type[..open]) + "~" + Token(type[(open + 1)..^1]) + "~";
    }
}
