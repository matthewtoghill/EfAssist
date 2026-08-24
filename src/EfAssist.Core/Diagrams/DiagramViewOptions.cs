namespace EfAssist.Core.Diagrams;

/// <summary>Which of the two diagrams to draw. One extraction feeds both.</summary>
public enum DiagramKind
{
    /// <summary>
    /// Tables, columns and foreign keys. Default, and first so <c>default(DiagramKind)</c> matches
    /// it — this is the view that pays for itself next to a migrations tool.
    /// </summary>
    EntityRelationship,

    /// <summary>CLR types, members and navigations, with inheritance drawn.</summary>
    Class,
}

/// <summary>How much of an entity's property list to show.</summary>
public enum PropertyDetail
{
    /// <summary>Everything. Default, and the reason the diagram is worth reading.</summary>
    All,

    /// <summary>Keys and foreign keys — enough to follow the relationships and nothing else.</summary>
    KeysAndForeignKeys,

    /// <summary>Primary and alternate keys only. For seeing the shape of a large model.</summary>
    KeysOnly,
}

/// <summary>
/// Everything about a diagram that is a display choice rather than a fact about the model. Changing
/// any of these re-renders; none of them re-extracts.
/// </summary>
public sealed record DiagramViewOptions
{
    public DiagramKind Kind { get; init; } = DiagramKind.EntityRelationship;

    public PropertyDetail Properties { get; init; } = PropertyDetail.All;

    /// <summary>Column type — or CLR type, in the class view — beside each row.</summary>
    public bool ShowTypes { get; init; } = true;

    /// <summary>A marker on nullable rows.</summary>
    public bool ShowNullability { get; init; } = true;

    /// <summary>Index rows under the properties. Off by default: useful, but noisy at a glance.</summary>
    public bool ShowIndexes { get; init; }

    /// <summary>
    /// Navigation rows. Meaningless in the ER view — a navigation is not a column — so it only
    /// applies to the class view, where it is the point.
    /// </summary>
    public bool ShowNavigations { get; init; } = true;

    /// <summary>
    /// Draw a many-to-many as one edge between its two ends rather than as the EF-generated join
    /// entity. On by default: the join table is an implementation detail of a relationship the reader
    /// already understands.
    /// </summary>
    public bool CollapseJoinEntities { get; init; } = true;

    /// <summary>
    /// Fold an owned reference's properties into its owner, which is where its columns actually live.
    /// Only applies when the two genuinely share a table — an owned collection has its own table and
    /// stays a node of its own whatever this says.
    /// </summary>
    public bool InlineOwnedTypes { get; init; } = true;

    /// <summary>Label each edge with its delete behaviour. Off by default; it is a lot of text.</summary>
    public bool ShowDeleteBehavior { get; init; }

    /// <summary>Draw the derived-to-base edges of a type-per-hierarchy mapping.</summary>
    public bool ShowInheritance { get; init; } = true;
}
