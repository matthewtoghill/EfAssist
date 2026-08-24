namespace EfAssist.Core.Diagrams;

/// <summary>What a row in a node represents.</summary>
public enum RowKind
{
    Property,
    Navigation,
    Index,
}

public enum EdgeKind
{
    /// <summary>An ordinary foreign key. Drawn from the dependent to the principal.</summary>
    ForeignKey,

    /// <summary>A many-to-many whose join entity has been collapsed away.</summary>
    ManyToMany,

    /// <summary>An owned type's relationship to its owner. Composition, not association.</summary>
    Ownership,

    /// <summary>Derived type to base type in a type-per-hierarchy mapping.</summary>
    Inheritance,
}

/// <param name="Badge">
/// The key role, as the short form a diagram shows: <c>PK</c>, <c>FK</c>, <c>PK FK</c>, <c>AK</c>,
/// <c>IX</c>, or empty.
/// </param>
/// <param name="IsNullable">
/// Drives the nullability marker. Always false for navigation and index rows, which have no
/// nullability of their own.
/// </param>
public sealed record DiagramRow(
    string Name,
    string? Type = null,
    string Badge = "",
    RowKind Kind = RowKind.Property,
    bool IsKey = false,
    bool IsForeignKey = false,
    bool IsNullable = false);

/// <param name="EntityName">
/// The <see cref="DiagramEntity.Name"/> this node came from. The identity used by selection, by
/// persisted positions and by edges, so it has to survive a re-layout unchanged.
/// </param>
/// <param name="Subtitle">The table, or the namespace in the class view. Null when there is nothing to add.</param>
public sealed record DiagramNode(
    string EntityName,
    string Title,
    string? Subtitle = null,
    IReadOnlyList<DiagramRow> Rows = null!,
    bool IsOwned = false,
    bool IsJoin = false,
    bool IsAbstractBase = false)
{
    public IReadOnlyList<DiagramRow> Rows { get; init; } = Rows ?? [];
}

/// <param name="From">The dependent, derived or owned end — where the edge starts.</param>
/// <param name="To">The principal, base or owner end.</param>
/// <param name="FromLabel">Cardinality at the dependent end, for example <c>*</c>.</param>
public sealed record DiagramEdge(
    string From,
    string To,
    EdgeKind Kind = EdgeKind.ForeignKey,
    string? Label = null,
    string? FromLabel = null,
    string? ToLabel = null);

/// <summary>
/// Turns a <see cref="DiagramModel"/> into the nodes and edges a layout can place, applying the
/// chosen <see cref="DiagramViewOptions"/>.
/// </summary>
/// <remarks>
/// The <em>only</em> place the entity-relationship and class views differ. Everything downstream —
/// layout, the scene, the renderer, every export — is shared. Adding a third view means adding a
/// row builder here and nothing else.
/// </remarks>
public static class DiagramNodeContent
{
    public sealed record Content(IReadOnlyList<DiagramNode> Nodes, IReadOnlyList<DiagramEdge> Edges);

    public static Content Build(DiagramModel model, DiagramViewOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);

        var hidden = HiddenEntities(model, options);
        var inlined = InlinedOwnedTypes(model, options);

        var nodes = model.Entities
            .Where(e => !hidden.Contains(e.Name))
            .Select(e => BuildNode(e, model, options, inlined.GetValueOrDefault(e.Name, [])))
            .ToList();

        var visible = nodes.Select(n => n.EntityName).ToHashSet(StringComparer.Ordinal);

        return new Content(nodes, [.. BuildEdges(model, options, visible, hidden)]);
    }

    // ---- Which entities disappear ----

    /// <summary>
    /// Entities the options fold away: collapsed many-to-many join tables, and owned references
    /// inlined into their owner.
    /// </summary>
    private static HashSet<string> HiddenEntities(DiagramModel model, DiagramViewOptions options)
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);

        if (options.CollapseJoinEntities)
        {
            foreach (var entity in model.Entities.Where(e => e.IsImplicitJoin))
            {
                hidden.Add(entity.Name);
            }
        }

        if (options.InlineOwnedTypes)
        {
            foreach (var entity in model.Entities.Where(e => IsInlineable(e, model)))
            {
                hidden.Add(entity.Name);
            }
        }

        return hidden;
    }

    /// <summary>
    /// An owned type whose columns live in the owner's table, so folding it in matches the database.
    /// An owned collection has a table of its own and is never inlined, whatever the option says.
    /// </summary>
    private static bool IsInlineable(DiagramEntity entity, DiagramModel model)
    {
        if (!entity.IsOwned || entity.OwnerName is null)
        {
            return false;
        }

        var ownership = model.Relationships.FirstOrDefault(r =>
            r.IsOwnership && r.DependentEntity == entity.Name);

        if (ownership?.Cardinality != Cardinality.OneToOne)
        {
            return false;
        }

        var owner = model.Entity(entity.OwnerName);
        return owner is not null && entity.Table is not null && entity.Table == owner.Table;
    }

    /// <summary>Owner name to the rows folded in from its inlined owned references.</summary>
    private static Dictionary<string, List<DiagramRow>> InlinedOwnedTypes(
        DiagramModel model, DiagramViewOptions options)
    {
        var result = new Dictionary<string, List<DiagramRow>>(StringComparer.Ordinal);
        if (!options.InlineOwnedTypes)
        {
            return result;
        }

        foreach (var owned in model.Entities.Where(e => IsInlineable(e, model)))
        {
            var ownership = model.Relationships.First(r =>
                r.IsOwnership && r.DependentEntity == owned.Name);

            var prefix = ownership.PrincipalNavigation ?? owned.ShortName;
            var rows = result.TryGetValue(owned.OwnerName!, out var existing) ? existing : [];

            // The owner's own key column reappears on the owned type as its foreign key; showing it
            // twice on one node is noise.
            foreach (var property in owned.Properties.Where(p =>
                !ownership.ForeignKeyProperties.Contains(p.Name)))
            {
                rows.Add(PropertyRow(property, options) with { Name = $"{prefix}.{property.Name}" });
            }

            result[owned.OwnerName!] = rows;
        }

        return result;
    }

    // ---- Nodes ----

    private static DiagramNode BuildNode(
        DiagramEntity entity,
        DiagramModel model,
        DiagramViewOptions options,
        IReadOnlyList<DiagramRow> inlinedRows)
    {
        var rows = new List<DiagramRow>();

        foreach (var property in entity.Properties)
        {
            if (Include(property, options))
            {
                rows.Add(PropertyRow(property, options));
            }
        }

        rows.AddRange(inlinedRows);

        if (options.Kind == DiagramKind.Class && options.ShowNavigations)
        {
            rows.AddRange(NavigationRows(entity, model, options));
        }

        if (options.ShowIndexes)
        {
            rows.AddRange(entity.Indexes.Select(index => new DiagramRow(
                index.DisplayName,
                Type: index.IsUnique ? "unique" : null,
                Badge: "IX",
                Kind: RowKind.Index)));
        }

        return new DiagramNode(
            entity.Name,
            Title: options.Kind == DiagramKind.EntityRelationship
                ? ResolvedTable(entity, model) ?? entity.ShortName
                : entity.ShortName,
            Subtitle: Subtitle(entity, model, options),
            Rows: rows,
            IsOwned: entity.IsOwned,
            IsJoin: entity.IsImplicitJoin,
            IsAbstractBase: model.Entities.Any(e => e.BaseType == entity.Name));
    }

    private static bool Include(DiagramProperty property, DiagramViewOptions options) =>
        options.Properties switch
        {
            PropertyDetail.KeysOnly => property.IsKey || property.IsAlternateKey,
            PropertyDetail.KeysAndForeignKeys =>
                property.IsKey || property.IsAlternateKey || property.IsForeignKey,
            _ => true,
        };

    private static DiagramRow PropertyRow(DiagramProperty property, DiagramViewOptions options) =>
        new(
            property.Name,
            Type: options.ShowTypes
                ? options.Kind == DiagramKind.Class ? property.ClrType : property.DisplayType
                : null,
            Badge: Badge(property),
            Kind: RowKind.Property,
            IsKey: property.IsKey,
            IsForeignKey: property.IsForeignKey,
            IsNullable: options.ShowNullability && !property.IsNotNull);

    private static string Badge(DiagramProperty property)
    {
        // Order matters: a composite key column that is also a foreign key is the normal shape of a
        // join table, and reading "PK FK" is the point.
        List<string> parts = [];
        if (property.IsKey)
        {
            parts.Add("PK");
        }

        if (property.IsForeignKey)
        {
            parts.Add("FK");
        }

        if (property.IsAlternateKey && !property.IsKey)
        {
            parts.Add("AK");
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Navigation rows for the class view, typed from the relationships they belong to — the snapshot
    /// records a navigation's name but never its CLR type, so the collection-or-not and the target
    /// come from which end of which relationship the name appears on.
    /// </summary>
    private static IEnumerable<DiagramRow> NavigationRows(
        DiagramEntity entity, DiagramModel model, DiagramViewOptions options)
    {
        foreach (var navigation in entity.Navigations)
        {
            yield return new DiagramRow(
                navigation,
                Type: options.ShowTypes ? NavigationType(entity, navigation, model) : null,
                Kind: RowKind.Navigation);
        }
    }

    private static string? NavigationType(
        DiagramEntity entity, string navigation, DiagramModel model)
    {
        // On the principal end the navigation points at the dependents, so it is a collection for
        // anything but a one-to-one.
        var asPrincipal = model.Relationships.FirstOrDefault(r =>
            r.PrincipalEntity == entity.Name && r.PrincipalNavigation == navigation);

        if (asPrincipal is not null)
        {
            var target = Short(asPrincipal.DependentEntity);
            return asPrincipal.Cardinality == Cardinality.OneToOne
                ? target
                : $"ICollection<{target}>";
        }

        // On the dependent end it points at the single principal.
        var asDependent = model.Relationships.FirstOrDefault(r =>
            r.DependentEntity == entity.Name && r.DependentNavigation == navigation);

        // Null when a navigation has no relationship carrying its name — a skip navigation whose
        // join entity was collapsed, for instance. Better an untyped row than a wrong type.
        return asDependent is null ? null : Short(asDependent.PrincipalEntity);
    }

    private static string? Subtitle(
        DiagramEntity entity, DiagramModel model, DiagramViewOptions options)
    {
        if (options.Kind == DiagramKind.EntityRelationship)
        {
            // The title is already the table, so the subtitle carries the type it maps from.
            return entity.IsImplicitJoin ? "join table" : entity.ShortName;
        }

        // In the class view the title is the type, so the subtitle carries where it lands.
        var table = ResolvedTable(entity, model);
        return entity.IsOwned && entity.OwnerName is not null
            ? $"owned by {Short(entity.OwnerName)}"
            : table;
    }

    /// <summary>
    /// The table an entity actually maps to. A type-per-hierarchy derived type declares none of its
    /// own and inherits its base's, so a node titled with the raw value would be blank.
    /// </summary>
    private static string? ResolvedTable(DiagramEntity entity, DiagramModel model)
    {
        var current = entity;
        var guard = 0;

        while (current.Table is null && current.BaseType is not null && guard++ < 16)
        {
            var next = model.Entity(current.BaseType);
            if (next is null)
            {
                break;
            }

            current = next;
        }

        return current.Table is null ? null : current.QualifiedTable;
    }

    // ---- Edges ----

    private static IEnumerable<DiagramEdge> BuildEdges(
        DiagramModel model,
        DiagramViewOptions options,
        HashSet<string> visible,
        HashSet<string> hidden)
    {
        foreach (var relationship in model.Relationships)
        {
            // A collapsed join entity's two foreign keys become one edge between its ends, emitted
            // once from the join entity rather than once per relationship.
            if (hidden.Contains(relationship.DependentEntity))
            {
                continue;
            }

            if (!visible.Contains(relationship.DependentEntity)
                || !visible.Contains(relationship.PrincipalEntity))
            {
                continue;
            }

            yield return new DiagramEdge(
                relationship.DependentEntity,
                relationship.PrincipalEntity,
                relationship.IsOwnership ? EdgeKind.Ownership : EdgeKind.ForeignKey,
                Label: options.ShowDeleteBehavior ? relationship.DeleteBehavior : null,
                FromLabel: relationship.Cardinality == Cardinality.OneToOne ? "1" : "*",
                ToLabel: relationship.IsRequired ? "1" : "0..1");
        }

        foreach (var edge in ManyToManyEdges(model, options, visible))
        {
            yield return edge;
        }

        if (!options.ShowInheritance)
        {
            yield break;
        }

        foreach (var entity in model.Entities.Where(e => e.BaseType is not null))
        {
            if (visible.Contains(entity.Name) && visible.Contains(entity.BaseType!))
            {
                yield return new DiagramEdge(entity.Name, entity.BaseType!, EdgeKind.Inheritance);
            }
        }
    }

    /// <summary>
    /// One edge per collapsed join entity, between the two entities it joins. Only emitted when the
    /// join entity is actually hidden — with <see cref="DiagramViewOptions.CollapseJoinEntities"/>
    /// off, the two foreign keys are drawn as themselves and a third edge would double them up.
    /// </summary>
    private static IEnumerable<DiagramEdge> ManyToManyEdges(
        DiagramModel model, DiagramViewOptions options, HashSet<string> visible)
    {
        if (!options.CollapseJoinEntities)
        {
            yield break;
        }

        foreach (var join in model.Entities.Where(e => e.IsImplicitJoin))
        {
            var ends = model.Relationships
                .Where(r => r.DependentEntity == join.Name && !r.IsOwnership)
                .Select(r => r.PrincipalEntity)
                .Where(visible.Contains)
                .ToList();

            if (ends.Count == 2)
            {
                yield return new DiagramEdge(
                    ends[0], ends[1], EdgeKind.ManyToMany, FromLabel: "*", ToLabel: "*");
            }
        }
    }

    private static string Short(string name)
    {
        // An owned type's name is Owner.Navigation#OwnedType, so the type is after the hash.
        var hash = name.LastIndexOf('#');
        var relevant = hash < 0 ? name : name[(hash + 1)..];
        var dot = relevant.LastIndexOf('.');
        return dot < 0 ? relevant : relevant[(dot + 1)..];
    }
}
