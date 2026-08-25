namespace EfAssist.Core.Diagrams;

/// <summary>How one thing in a diagram differs from the model it is being compared against.</summary>
public enum DiagramChange
{
    /// <summary>Unchanged, or nothing to compare against. Default, so no-diff is the zero value.</summary>
    None,

    Added,
    Removed,

    /// <summary>
    /// Present in both, but not identical — a retyped column, a new length, a nullability change.
    /// Only ever set on property rows and on the entities that contain them; the model has no notion
    /// of a modified relationship, so a changed foreign key reads as one removed and one added.
    /// </summary>
    Modified,
}

/// <summary>
/// A merged model and the changes within it. The model holds everything either side had, so a
/// removed entity or column still has something to draw.
/// </summary>
public sealed record DiagramComparison(DiagramModel Model, DiagramDiff Diff);

/// <summary>
/// What changed between two <see cref="DiagramModel"/>s — normally the snapshot in a migration's
/// <c>.Designer.cs</c> and the one in the migration before it.
/// </summary>
/// <remarks>
/// <para>
/// Names, not identities: an entity, a property or a relationship is matched by name across the two
/// models. A rename therefore reads as one removal and one addition, which is exactly what the
/// generated migration does to the database, so it is the honest answer rather than a limitation.
/// </para>
/// <para>
/// Keyed lookups rather than a tree, because the consumer walks the merged model and asks about each
/// thing it reaches. See <see cref="DiagramNodeContent.Build"/>, which is the only caller that
/// matters — everything downstream reads <see cref="DiagramRow.Change"/> instead.
/// </para>
/// </remarks>
public sealed record DiagramDiff(
    IReadOnlyDictionary<string, DiagramChange> Entities,
    IReadOnlyDictionary<string, DiagramChange> Rows,
    IReadOnlyDictionary<string, DiagramChange> Edges)
{
    /// <summary>No comparison was made. Every lookup answers <see cref="DiagramChange.None"/>.</summary>
    public static DiagramDiff Empty { get; } = new(
        new Dictionary<string, DiagramChange>(),
        new Dictionary<string, DiagramChange>(),
        new Dictionary<string, DiagramChange>());

    public bool IsEmpty => Entities.Count == 0 && Rows.Count == 0 && Edges.Count == 0;

    public DiagramChange ForEntity(string entityName) =>
        Entities.GetValueOrDefault(entityName, DiagramChange.None);

    public DiagramChange ForRow(string entityName, string rowName) =>
        Rows.GetValueOrDefault(RowKey(entityName, rowName), DiagramChange.None);

    /// <summary>
    /// The change on the relationship between two entities, whichever end is named first — a
    /// collapsed many-to-many edge does not know which of its two ends was the dependent.
    /// </summary>
    public DiagramChange ForEdge(string from, string to) =>
        Edges.GetValueOrDefault(EdgeKey(from, to), DiagramChange.None);

    /// <summary>How many entities carry a given change. For the counts on the diff legend.</summary>
    public int EntityCount(DiagramChange change) => Entities.Values.Count(c => c == change);

    public int RowCount(DiagramChange change) => Rows.Values.Count(c => c == change);

    /// <summary>
    /// The changes as a sentence: <c>"+2 tables, −1 table, +5 columns"</c>. Empty when nothing
    /// changed, so a caller can say so in its own words.
    /// </summary>
    public string Summary
    {
        get
        {
            List<string> parts =
            [
                .. Part(EntityCount(DiagramChange.Added), "+", "table"),
                .. Part(EntityCount(DiagramChange.Removed), "−", "table"),
                .. Part(RowCount(DiagramChange.Added), "+", "column"),
                .. Part(RowCount(DiagramChange.Removed), "−", "column"),
                .. Part(RowCount(DiagramChange.Modified), "~", "column"),
            ];

            return string.Join(", ", parts);

            static IEnumerable<string> Part(int count, string sign, string noun) =>
                count == 0 ? [] : [$"{sign}{count} {noun}{(count == 1 ? "" : "s")}"];
        }
    }

    /// <summary>
    /// Compares two models and merges them, so the result can be drawn as one diagram with the
    /// differences marked.
    /// </summary>
    /// <param name="previous">
    /// The earlier model. Null means there is no earlier one — the first migration, where every
    /// entity is genuinely new and marking them all as added is the truth rather than noise.
    /// </param>
    /// <param name="current">The model being looked at, and the one the merged model is based on.</param>
    public static DiagramComparison Compare(DiagramModel? previous, DiagramModel current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var entities = new Dictionary<string, DiagramChange>(StringComparer.Ordinal);
        var rows = new Dictionary<string, DiagramChange>(StringComparer.Ordinal);
        var edges = new Dictionary<string, DiagramChange>(StringComparer.Ordinal);

        if (previous is null)
        {
            foreach (var entity in current.Entities)
            {
                entities[entity.Name] = DiagramChange.Added;
                foreach (var name in RowNames(entity))
                {
                    rows[RowKey(entity.Name, name)] = DiagramChange.Added;
                }
            }

            foreach (var relationship in current.Relationships)
            {
                edges[EdgeKey(relationship.DependentEntity, relationship.PrincipalEntity)] =
                    DiagramChange.Added;
            }

            return new DiagramComparison(current, new DiagramDiff(entities, rows, edges));
        }

        var before = Index(previous.Entities);
        var after = Index(current.Entities);
        var merged = new List<DiagramEntity>(current.Entities.Count);

        foreach (var entity in current.Entities)
        {
            if (!before.TryGetValue(entity.Name, out var old))
            {
                entities[entity.Name] = DiagramChange.Added;
                foreach (var name in RowNames(entity))
                {
                    rows[RowKey(entity.Name, name)] = DiagramChange.Added;
                }

                merged.Add(entity);
                continue;
            }

            merged.Add(MergeEntity(old, entity, rows, entities));
        }

        foreach (var old in previous.Entities.Where(e => !after.ContainsKey(e.Name)))
        {
            entities[old.Name] = DiagramChange.Removed;
            foreach (var name in RowNames(old))
            {
                rows[RowKey(old.Name, name)] = DiagramChange.Removed;
            }

            merged.Add(old);
        }

        var mergedRelationships = MergeRelationships(previous, current, edges);

        return new DiagramComparison(
            current with { Entities = merged, Relationships = mergedRelationships },
            new DiagramDiff(entities, rows, edges));
    }

    /// <summary>
    /// One entity as it exists in both models: its current shape, with anything the earlier model had
    /// and this one does not appended so it still has a row to draw.
    /// </summary>
    private static DiagramEntity MergeEntity(
        DiagramEntity old,
        DiagramEntity current,
        Dictionary<string, DiagramChange> rows,
        Dictionary<string, DiagramChange> entities)
    {
        var oldProperties = old.Properties.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var changed = false;

        foreach (var property in current.Properties)
        {
            if (!oldProperties.TryGetValue(property.Name, out var was))
            {
                rows[RowKey(current.Name, property.Name)] = DiagramChange.Added;
                changed = true;
            }
            else if (was != property)
            {
                // Record equality, so any facet the parser reads — type, length, default, key role —
                // counts. Cheaper and more complete than listing the ones worth comparing.
                rows[RowKey(current.Name, property.Name)] = DiagramChange.Modified;
                changed = true;
            }
        }

        var currentProperties = current.Properties
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        var goneProperties = old.Properties
            .Where(p => !currentProperties.Contains(p.Name))
            .ToList();

        foreach (var property in goneProperties)
        {
            rows[RowKey(current.Name, property.Name)] = DiagramChange.Removed;
        }

        foreach (var navigation in current.Navigations.Where(n => !old.Navigations.Contains(n)))
        {
            rows[RowKey(current.Name, navigation)] = DiagramChange.Added;
            changed = true;
        }

        var goneNavigations = old.Navigations
            .Where(n => !current.Navigations.Contains(n))
            .ToList();

        foreach (var navigation in goneNavigations)
        {
            rows[RowKey(current.Name, navigation)] = DiagramChange.Removed;
        }

        if (changed || goneProperties.Count > 0 || goneNavigations.Count > 0)
        {
            entities[current.Name] = DiagramChange.Modified;
        }

        if (goneProperties.Count == 0 && goneNavigations.Count == 0)
        {
            return current;
        }

        return current with
        {
            // Appended rather than in their old positions: the current shape is what the reader is
            // looking at, and a removed column at the bottom is easier to find than one buried in
            // the middle. Indexes are deliberately not merged — an index is derived from columns, so
            // a dropped one shows up as its dropped column.
            Properties = [.. current.Properties, .. goneProperties],
            Navigations = [.. current.Navigations, .. goneNavigations],
        };
    }

    /// <summary>
    /// Every relationship either model had, with the ones only one of them had recorded as a change.
    /// </summary>
    private static List<DiagramRelationship> MergeRelationships(
        DiagramModel previous, DiagramModel current, Dictionary<string, DiagramChange> edges)
    {
        var before = previous.Relationships
            .ToDictionary(RelationshipKey, r => r, StringComparer.Ordinal);

        var after = current.Relationships
            .ToDictionary(RelationshipKey, r => r, StringComparer.Ordinal);

        var merged = new List<DiagramRelationship>(current.Relationships);

        foreach (var relationship in after.Where(p => !before.ContainsKey(p.Key)).Select(p => p.Value))
        {
            edges[EdgeKey(relationship.DependentEntity, relationship.PrincipalEntity)] =
                DiagramChange.Added;
        }

        foreach (var relationship in before.Where(p => !after.ContainsKey(p.Key)).Select(p => p.Value))
        {
            var edge = EdgeKey(relationship.DependentEntity, relationship.PrincipalEntity);

            // An added relationship between the same two entities wins: a foreign key that moved
            // columns is one edge on screen, and calling it added says the more useful thing.
            if (!edges.ContainsKey(edge))
            {
                edges[edge] = DiagramChange.Removed;
            }

            merged.Add(relationship);
        }

        return merged;
    }

    /// <summary>The marker a diagram prefixes a changed row or title with, so a print still reads.</summary>
    public static string Marker(DiagramChange change) => change switch
    {
        DiagramChange.Added => "+ ",
        DiagramChange.Removed => "− ",
        DiagramChange.Modified => "~ ",
        _ => "",
    };

    private static Dictionary<string, DiagramEntity> Index(IReadOnlyList<DiagramEntity> entities)
    {
        var index = new Dictionary<string, DiagramEntity>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            // Last wins rather than throwing. A snapshot should never declare a name twice, but a
            // hand-edited one might, and a duplicate is not worth failing a diff over.
            index[entity.Name] = entity;
        }

        return index;
    }

    private static IEnumerable<string> RowNames(DiagramEntity entity) =>
        entity.Properties.Select(p => p.Name).Concat(entity.Navigations);

    /// <summary>A separator no C# identifier or type name can contain, so keys cannot collide.</summary>
    private const string Separator = "\u0001";

    private static string RowKey(string entityName, string rowName) =>
        entityName + Separator + rowName;

    /// <summary>
    /// A relationship's identity: its two ends and the columns it is on. The columns are part of it
    /// because a foreign key moved to a different column is a different foreign key.
    /// </summary>
    private static string RelationshipKey(DiagramRelationship relationship) =>
        string.Join(
            Separator,
            relationship.DependentEntity,
            relationship.PrincipalEntity,
            string.Join(',', relationship.ForeignKeyProperties));

    private static string EdgeKey(string from, string to) =>
        string.CompareOrdinal(from, to) <= 0 ? from + Separator + to : to + Separator + from;
}
