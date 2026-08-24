namespace EfAssist.Core.Diagrams;

/// <summary>How many rows on each end of a relationship.</summary>
public enum Cardinality
{
    /// <summary>One principal, many dependents. The common case.</summary>
    OneToMany,

    OneToOne,

    /// <summary>Skip navigations on both ends. Drawn either through the join entity or as one edge.</summary>
    ManyToMany,
}

/// <summary>
/// The model behind a diagram, extracted from an EF model snapshot. Plain records with no UI
/// concepts, so this doubles as the JSON export format and the persisted payload.
/// </summary>
/// <remarks>
/// This is the <em>relational</em> model as of the migration whose snapshot it came from — not the
/// current state of the code. Anything unmapped is absent by design, and pending model changes are
/// invisible here. See <c>docs/DIAGRAMS-PLAN.md</c> §2.2.
/// </remarks>
/// <param name="EfVersion">
/// From <c>modelBuilder.HasAnnotation("ProductVersion", …)</c>. Null on a snapshot that omits it.
/// </param>
/// <param name="SourceHash">
/// SHA-256 of the snapshot file, hex. The whole point is staleness detection: a persisted diagram
/// re-hashes its source on load and badges itself when the two disagree.
/// </param>
public sealed record DiagramModel(
    string ContextName,
    string SourcePath,
    string SourceHash,
    string? EfVersion,
    IReadOnlyList<DiagramEntity> Entities,
    IReadOnlyList<DiagramRelationship> Relationships)
{
    public static DiagramModel Empty { get; } = new("", "", "", null, [], []);

    public DiagramEntity? Entity(string name) =>
        Entities.FirstOrDefault(e => e.Name == name);
}

/// <param name="Name">
/// The name EF uses in the snapshot: normally the full CLR type name, but an implicit
/// many-to-many join entity has no CLR type and gets a bare name like <c>PostTag</c>.
/// </param>
/// <param name="Table">
/// Null on a type-per-hierarchy derived type, which inherits its base's table rather than declaring
/// one. Resolve through <paramref name="BaseType"/> when displaying it.
/// </param>
/// <param name="IsImplicitJoin">
/// An EF-generated join entity for a many-to-many with skip navigations. Detected rather than
/// declared — see <see cref="ModelSnapshotParser"/>.
/// </param>
public sealed record DiagramEntity(
    string Name,
    string? Table = null,
    string? Schema = null,
    string? BaseType = null,
    string? DiscriminatorProperty = null,
    string? DiscriminatorValue = null,
    bool IsOwned = false,
    bool IsImplicitJoin = false,
    string? OwnerName = null,
    IReadOnlyList<DiagramProperty> Properties = null!,
    IReadOnlyList<DiagramIndex> Indexes = null!,
    IReadOnlyList<string> Keys = null!,
    IReadOnlyList<IReadOnlyList<string>> AlternateKeys = null!,
    IReadOnlyList<string> Navigations = null!)
{
    public IReadOnlyList<DiagramProperty> Properties { get; init; } = Properties ?? [];
    public IReadOnlyList<DiagramIndex> Indexes { get; init; } = Indexes ?? [];
    public IReadOnlyList<string> Keys { get; init; } = Keys ?? [];
    public IReadOnlyList<IReadOnlyList<string>> AlternateKeys { get; init; } = AlternateKeys ?? [];
    public IReadOnlyList<string> Navigations { get; init; } = Navigations ?? [];

    /// <summary>The last segment of <see cref="Name"/> — what a diagram node is titled with.</summary>
    public string ShortName
    {
        get
        {
            var dot = Name.LastIndexOf('.');
            return dot < 0 ? Name : Name[(dot + 1)..];
        }
    }

    public string? Namespace
    {
        get
        {
            var dot = Name.LastIndexOf('.');
            return dot < 0 ? null : Name[..dot];
        }
    }

    /// <summary>Table name qualified with its schema, when there is one.</summary>
    public string? QualifiedTable => Table is null
        ? null
        : Schema is null ? Table : $"{Schema}.{Table}";
}

/// <param name="ClrType">
/// As written in <c>Property&lt;T&gt;</c>, so <c>int?</c> stays nullable and <c>DateTime</c> is not
/// expanded to its full name. Verbatim is more readable than normalised here.
/// </param>
/// <param name="IsRequired">
/// From <c>IsRequired()</c> on the property chain. Note EF omits it for non-nullable value types,
/// which are required anyway — <see cref="IsNotNull"/> is the question a diagram wants answered.
/// </param>
public sealed record DiagramProperty(
    string Name,
    string ClrType,
    string? ColumnName = null,
    string? ColumnType = null,
    bool IsRequired = false,
    bool IsKey = false,
    bool IsForeignKey = false,
    bool IsAlternateKey = false,
    bool IsDiscriminator = false,
    int? MaxLength = null,
    string? DefaultValue = null,
    string? DefaultValueSql = null,
    string? ValueGenerated = null)
{
    /// <summary>
    /// Whether the column is non-nullable.
    /// </summary>
    /// <remarks>
    /// A snapshot never states this directly. EF writes <c>IsRequired()</c> only where the answer is
    /// not already implied, so three signals have to be combined: a key is never nullable, a
    /// <c>T?</c> value type always is, and a reference type is nullable precisely when
    /// <c>IsRequired()</c> is absent — <c>Property&lt;string&gt;("Url")</c> with nothing after it is
    /// an optional column, while <c>Property&lt;int&gt;("Views")</c> with nothing after it is not.
    /// </remarks>
    public bool IsNotNull =>
        IsRequired || IsKey || (!ClrType.EndsWith('?') && !IsReferenceType);

    /// <summary>
    /// Whether <see cref="ClrType"/> names a reference type, which decides how the absence of
    /// <c>IsRequired()</c> reads.
    /// </summary>
    /// <remarks>
    /// ponytail: a name list, because a syntax-only parse has no semantic model to ask. It covers
    /// what EF actually emits for a scalar property — everything else a snapshot declares is a value
    /// type, since reference-typed members are navigations rather than properties. If a mapped
    /// complex type ever shows up here it will read as non-nullable; add it to the list then.
    /// </remarks>
    private bool IsReferenceType
    {
        get
        {
            var type = ClrType.TrimEnd('?');
            return type.EndsWith("[]", StringComparison.Ordinal)
                || type is "string" or "String" or "System.String"
                or "object" or "Object" or "System.Object";
        }
    }

    /// <summary>What a diagram row shows for the type: the column type if known, else the CLR type.</summary>
    public string DisplayType => ColumnType ?? ClrType;
}

/// <param name="DatabaseName">
/// From <c>HasDatabaseName</c> (or the older <c>HasName</c>). Null means EF's conventional name,
/// which the snapshot does not spell out.
/// </param>
public sealed record DiagramIndex(
    IReadOnlyList<string> Properties,
    bool IsUnique = false,
    string? DatabaseName = null,
    string? Filter = null)
{
    public string DisplayName => DatabaseName ?? string.Join(", ", Properties);
}

/// <param name="ForeignKeyProperties">
/// The columns on the dependent. Empty only on a malformed snapshot.
/// </param>
/// <param name="IsOwnership">
/// The dependent is an owned type. Drawn as composition rather than as an ordinary foreign key.
/// </param>
/// <param name="IsRequired">
/// From <c>IsRequired()</c> on the <em>relationship</em> chain, which is a different thing from
/// <c>IsRequired()</c> on a property chain despite the identical name.
/// </param>
public sealed record DiagramRelationship(
    string PrincipalEntity,
    string DependentEntity,
    IReadOnlyList<string> ForeignKeyProperties,
    IReadOnlyList<string>? PrincipalKeyProperties = null,
    string? PrincipalNavigation = null,
    string? DependentNavigation = null,
    string? DeleteBehavior = null,
    Cardinality Cardinality = Cardinality.OneToMany,
    bool IsOwnership = false,
    bool IsRequired = false)
{
    public bool IsSelfReference => PrincipalEntity == DependentEntity;
}
