using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfAssist.Core.Diagrams;

/// <summary>
/// Reads an EF model snapshot — <c>&lt;Context&gt;ModelSnapshot.cs</c> or a migration's
/// <c>.Designer.cs</c> — into a <see cref="DiagramModel"/>.
/// </summary>
/// <remarks>
/// <para>
/// A syntax-only Roslyn parse. No compilation and no semantic model, so nothing here needs the
/// target project to build, to restore, or even to be valid C#: a snapshot that fails to compile
/// still parses.
/// </para>
/// <para>
/// Everything is driven off <em>invocation chains</em> rather than method names in isolation, because
/// the same name means different things in different positions. <c>IsRequired()</c> on a
/// <c>Property</c> chain is a non-nullable column; the identical call on a <c>HasOne</c> chain is a
/// required relationship. <c>HasForeignKey("BlogId")</c> names one property, while
/// <c>HasForeignKey("Ns.Type", "PostId")</c> names the dependent type first and the property second.
/// Only the chain tells the two apart.
/// </para>
/// <para>
/// ponytail: unrecognised calls are skipped silently, never fatal. A future EF version that adds a
/// fluent method should cost the diagram that one detail, not the whole tab — see
/// <c>Fixtures/snapshot-future.txt</c>. Not worth upgrading for correctness; if a silently-dropped
/// construct ever turns out to matter, surface it as a note on the diagram rather than an error.
/// </para>
/// </remarks>
public static class ModelSnapshotParser
{
    /// <param name="source">The snapshot file's contents.</param>
    /// <param name="sourcePath">Recorded on the model, and what staleness checks re-hash.</param>
    /// <param name="contextName">
    /// Overrides the name read from the file's <c>[DbContext(typeof(X))]</c> attribute. Only useful
    /// when the caller already knows better.
    /// </param>
    public static DiagramModel Parse(
        string source,
        string sourcePath = "",
        string? contextName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var root = CSharpSyntaxTree.ParseText(source, cancellationToken: cancellationToken)
            .GetRoot(cancellationToken);

        var state = new ParseState();

        foreach (var statement in Statements(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadRootCall(statement, state, cancellationToken);
        }

        var model = new DiagramModel(
            contextName ?? ContextNameFrom(root) ?? "",
            sourcePath,
            Hash(source),
            state.EfVersion,
            [.. state.Entities.Select(e => e.Build())],
            state.Relationships);

        return Classify(model);
    }

    /// <summary>Hex SHA-256 of the snapshot text, for the staleness check on a persisted diagram.</summary>
    public static string Hash(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));

    /// <summary>
    /// The context this snapshot belongs to, from its <c>[DbContext(typeof(X))]</c> attribute.
    /// Returns the short type name — the attribute is the authoritative link between a snapshot file
    /// and a context, and it survives the file being renamed while the class is not.
    /// </summary>
    public static string? ContextNameFrom(SyntaxNode root) => root
        .DescendantNodes()
        .OfType<AttributeSyntax>()
        .Where(a => Name(a.Name) is "DbContext" or "DbContextAttribute")
        .SelectMany(a => a.ArgumentList?.Arguments ?? default)
        .Select(a => a.Expression)
        .OfType<TypeOfExpressionSyntax>()
        .Select(t => Name(t.Type))
        .FirstOrDefault(n => n.Length > 0);

    /// <summary>Convenience overload for callers holding the file text rather than a tree.</summary>
    public static string? ContextNameFromSource(string source) =>
        ContextNameFrom(CSharpSyntaxTree.ParseText(source).GetRoot());

    // ---- Root scope: modelBuilder.* ----

    private static void ReadRootCall(
        ExpressionStatementSyntax statement, ParseState state, CancellationToken cancellationToken)
    {
        var chain = Chain.From(statement.Expression);
        if (chain is null || chain.Receiver != "modelBuilder")
        {
            return;
        }

        var first = chain.Calls[0];
        switch (first.Name)
        {
            case "HasAnnotation"
                when first.Text(0) == "ProductVersion" && first.Text(1) is { } version:
                state.EfVersion = version;
                break;

            case "Entity" when first.Text(0) is { } entityName:
                // EF emits several Entity blocks for the same type — properties and keys in one,
                // foreign keys in a later one, navigations in a later one still. Merging by name
                // rather than creating an entity per block is the difference between eight entities
                // and twenty.
                var entity = state.GetOrAdd(entityName);
                ReadEntityBlock(first.Lambda, entity, state, cancellationToken);
                break;
        }
    }

    // ---- Entity scope: b.* ----

    private static void ReadEntityBlock(
        LambdaExpressionSyntax? lambda,
        EntityBuilder entity,
        ParseState state,
        CancellationToken cancellationToken)
    {
        if (lambda is null)
        {
            return;
        }

        var builder = Parameter(lambda);

        foreach (var statement in Statements(lambda))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chain = Chain.From(statement.Expression);
            if (chain is null || chain.Receiver != builder)
            {
                continue;
            }

            ReadEntityCall(chain, entity, state, cancellationToken);
        }
    }

    private static void ReadEntityCall(
        Chain chain, EntityBuilder entity, ParseState state, CancellationToken cancellationToken)
    {
        var first = chain.Calls[0];

        switch (first.Name)
        {
            case "Property":
                ReadProperty(chain, entity);
                break;

            case "HasKey":
                entity.Keys = first.Texts();
                break;

            case "HasAlternateKey":
                entity.AlternateKeys.Add(first.Texts());
                break;

            case "HasIndex":
                entity.Indexes.Add(ReadIndex(chain));
                break;

            case "ToTable" or "ToView" or "ToSqlQuery" or "ToFunction":
                // ToTable("Blogs", (string)null) is EF's own output on some paths, so a null second
                // argument has to read as "no schema" rather than as a schema called "null".
                entity.Table = first.Text(0) ?? entity.Table;
                entity.Schema = first.Text(1) ?? entity.Schema;
                break;

            case "HasBaseType":
                entity.BaseType = first.Text(0);
                break;

            case "HasDiscriminator":
                // Two forms: HasDiscriminator<string>("Prop") on the base, naming the column, and
                // HasDiscriminator().HasValue("x") on a derived type, naming only the value.
                entity.DiscriminatorProperty = first.Text(0) ?? entity.DiscriminatorProperty;
                entity.DiscriminatorValue =
                    chain.Calls.FirstOrDefault(c => c.Name == "HasValue")?.Text(0)
                    ?? entity.DiscriminatorValue;
                break;

            case "Navigation" when first.Text(0) is { } navigation:
                if (!entity.Navigations.Contains(navigation))
                {
                    entity.Navigations.Add(navigation);
                }

                break;

            case "HasOne":
                state.Relationships.Add(ReadHasOne(chain, entity.Name));
                break;

            case "HasMany":
                state.Relationships.Add(ReadHasMany(chain, entity.Name));
                break;

            case "OwnsOne" or "OwnsMany":
                ReadOwned(chain, entity, state, cancellationToken);
                break;

            case "WithOwner":
                // Only appears inside an owned type's own block, and carries the foreign key back to
                // the owner. The ownership relationship itself was created by ReadOwned.
                ReadWithOwner(chain, entity, state);
                break;
        }
    }

    private static void ReadProperty(Chain chain, EntityBuilder entity)
    {
        var first = chain.Calls[0];
        if (first.Text(0) is not { } name)
        {
            return;
        }

        var property = new DiagramProperty(name, first.TypeArgument ?? "object");

        foreach (var call in chain.Calls.Skip(1))
        {
            property = call.Name switch
            {
                "IsRequired" => property with { IsRequired = call.Bool(0) },
                "HasColumnName" => property with { ColumnName = call.Text(0) },
                "HasColumnType" => property with { ColumnType = call.Text(0) },
                "HasMaxLength" => property with { MaxLength = call.Int(0) },
                "HasDefaultValue" => property with { DefaultValue = call.Text(0) },
                "HasDefaultValueSql" => property with { DefaultValueSql = call.Text(0) },
                "ValueGeneratedOnAdd" => property with { ValueGenerated = "OnAdd" },
                "ValueGeneratedOnUpdate" => property with { ValueGenerated = "OnUpdate" },
                "ValueGeneratedOnAddOrUpdate" => property with { ValueGenerated = "OnAddOrUpdate" },
                _ => property,
            };
        }

        entity.SetProperty(property);
    }

    private static DiagramIndex ReadIndex(Chain chain)
    {
        var index = new DiagramIndex(chain.Calls[0].Texts());

        foreach (var call in chain.Calls.Skip(1))
        {
            index = call.Name switch
            {
                "IsUnique" => index with { IsUnique = call.Bool(0) },
                // HasName is the pre-EF5 spelling. Both still turn up in committed snapshots.
                "HasDatabaseName" or "HasName" => index with { DatabaseName = call.Text(0) },
                "HasFilter" => index with { Filter = call.Text(0) },
                _ => index,
            };
        }

        return index;
    }

    /// <summary>
    /// <c>b.HasOne(principalType, navigationOnThisEntity).WithMany(navigationOnPrincipal)…</c> —
    /// the entity owning the block is the dependent.
    /// </summary>
    private static DiagramRelationship ReadHasOne(Chain chain, string dependent)
    {
        var first = chain.Calls[0];
        var relationship = new DiagramRelationship(
            PrincipalEntity: first.Text(0) ?? "",
            DependentEntity: dependent,
            ForeignKeyProperties: [],
            DependentNavigation: first.Text(1));

        return ApplyRelationshipChain(chain, relationship, dependent);
    }

    /// <summary>
    /// <c>b.HasMany(dependentType, navigation).WithMany(...)</c> — here the entity owning the block
    /// is the principal, so the ends are the other way round. Rare in snapshots, where EF usually
    /// writes the join entity out explicitly instead.
    /// </summary>
    private static DiagramRelationship ReadHasMany(Chain chain, string principal)
    {
        var first = chain.Calls[0];
        var relationship = new DiagramRelationship(
            PrincipalEntity: principal,
            DependentEntity: first.Text(0) ?? "",
            ForeignKeyProperties: [],
            PrincipalNavigation: first.Text(1));

        return ApplyRelationshipChain(chain, relationship, first.Text(0) ?? "");
    }

    private static DiagramRelationship ApplyRelationshipChain(
        Chain chain, DiagramRelationship relationship, string dependent)
    {
        foreach (var call in chain.Calls.Skip(1))
        {
            relationship = call.Name switch
            {
                "WithMany" => relationship with
                {
                    PrincipalNavigation = call.Text(0) ?? relationship.PrincipalNavigation,
                    Cardinality = chain.Calls[0].Name == "HasMany"
                        ? Cardinality.ManyToMany
                        : Cardinality.OneToMany,
                },
                "WithOne" => relationship with
                {
                    PrincipalNavigation = call.Text(0) ?? relationship.PrincipalNavigation,
                    Cardinality = Cardinality.OneToOne,
                },
                "HasForeignKey" => relationship with
                {
                    ForeignKeyProperties = KeyProperties(call, dependent),
                },
                "HasPrincipalKey" => relationship with
                {
                    PrincipalKeyProperties = KeyProperties(call, relationship.PrincipalEntity),
                },
                "OnDelete" => relationship with { DeleteBehavior = call.Member(0) },
                "IsRequired" => relationship with { IsRequired = call.Bool(0) },
                _ => relationship,
            };
        }

        return relationship;
    }

    /// <summary>
    /// The property names out of a <c>HasForeignKey</c> / <c>HasPrincipalKey</c> call. On a
    /// one-to-one EF writes the owning type first —
    /// <c>HasForeignKey("Ns.PostStatistics", "PostId")</c> — and that leading type name is not a
    /// column. Dropping it by comparing against the entity name is exact, where "looks like a type
    /// name" would be a guess.
    /// </summary>
    private static IReadOnlyList<string> KeyProperties(Call call, string entityName)
    {
        var names = call.Texts();
        return names.Count > 1 && names[0] == entityName ? names.Skip(1).ToList() : names;
    }

    /// <summary>
    /// <c>b.OwnsOne(ownedType, navigation, b1 => { … })</c>. The owned type gets its own entity and
    /// an ownership relationship, and its nested block is read in the same way as any other entity's.
    /// </summary>
    private static void ReadOwned(
        Chain chain, EntityBuilder owner, ParseState state, CancellationToken cancellationToken)
    {
        var first = chain.Calls[0];
        if (first.Text(0) is not { } ownedType)
        {
            return;
        }

        var navigation = first.Text(1);

        // EF's own name for an owned type in the model: Owner.Navigation#OwnedType. Two entities can
        // own the same type — a Contractor and an Employee both owning an Address is the textbook
        // case — so keying on the bare type name would merge them into one node.
        var name = navigation is null
            ? $"{owner.Name}#{ownedType}"
            : $"{owner.Name}.{navigation}#{ownedType}";

        var owned = state.GetOrAdd(name);
        owned.IsOwned = true;
        owned.OwnerName = owner.Name;

        state.Relationships.Add(new DiagramRelationship(
            PrincipalEntity: owner.Name,
            DependentEntity: name,
            ForeignKeyProperties: [],
            PrincipalNavigation: navigation,
            Cardinality: first.Name == "OwnsMany" ? Cardinality.OneToMany : Cardinality.OneToOne,
            IsOwnership: true,
            IsRequired: true));

        ReadEntityBlock(first.Lambda, owned, state, cancellationToken);
    }

    /// <summary>
    /// Fills in the foreign key on an ownership relationship created by <see cref="ReadOwned"/>.
    /// The owned type's block says <c>b1.WithOwner().HasForeignKey("AuthorId")</c>, which is the only
    /// place those columns are named.
    /// </summary>
    private static void ReadWithOwner(Chain chain, EntityBuilder owned, ParseState state)
    {
        var call = chain.Calls.FirstOrDefault(c => c.Name == "HasForeignKey");
        if (call is null)
        {
            return;
        }

        var properties = KeyProperties(call, owned.Name);
        if (properties.Count == 0)
        {
            return;
        }

        for (var i = 0; i < state.Relationships.Count; i++)
        {
            if (state.Relationships[i] is { IsOwnership: true } existing
                && existing.DependentEntity == owned.Name)
            {
                state.Relationships[i] = existing with { ForeignKeyProperties = properties };
                return;
            }
        }
    }

    // ---- Post-processing ----

    /// <summary>
    /// The parts that can only be worked out once every block has been read: which properties are
    /// foreign keys, which entities are EF-generated many-to-many join tables, and which properties
    /// carry a key or discriminator role.
    /// </summary>
    private static DiagramModel Classify(DiagramModel model)
    {
        var foreignKeys = model.Relationships
            .SelectMany(r => r.ForeignKeyProperties.Select(p => (r.DependentEntity, Property: p)))
            .ToHashSet();

        var entities = model.Entities.Select(entity =>
        {
            var alternateKeys = entity.AlternateKeys.SelectMany(k => k).ToHashSet(StringComparer.Ordinal);

            return entity with
            {
                IsImplicitJoin = IsImplicitJoin(entity, model),
                Properties = [.. entity.Properties.Select(p => p with
                {
                    IsKey = entity.Keys.Contains(p.Name),
                    IsForeignKey = foreignKeys.Contains((entity.Name, p.Name)),
                    IsAlternateKey = alternateKeys.Contains(p.Name),
                    IsDiscriminator = p.Name == entity.DiscriminatorProperty,
                })],
            };
        });

        return model with { Entities = [.. entities] };
    }

    /// <summary>
    /// A many-to-many join entity that EF invented, rather than one the user wrote. Three signals
    /// together, because each alone has a false positive: no namespace (EF names these after the two
    /// ends, with no CLR type behind them), exactly two outgoing foreign keys, and a composite
    /// primary key made of precisely those foreign key columns and nothing else.
    /// </summary>
    /// <remarks>
    /// The last condition is what rejects a hand-written join entity carrying a payload — the
    /// <c>BlogEditor</c> in <c>samples/SampleRichModel</c> exists to be that near miss.
    /// </remarks>
    private static bool IsImplicitJoin(DiagramEntity entity, DiagramModel model)
    {
        if (entity.IsOwned || entity.Name.Contains('.') || entity.Keys.Count != 2)
        {
            return false;
        }

        var foreignKeys = model.Relationships
            .Where(r => r.DependentEntity == entity.Name && !r.IsOwnership)
            .SelectMany(r => r.ForeignKeyProperties)
            .ToList();

        return foreignKeys.Count == 2
            && entity.Keys.All(foreignKeys.Contains)
            && entity.Properties.Count == 2;
    }

    // ---- Syntax helpers ----

    private static IEnumerable<ExpressionStatementSyntax> Statements(SyntaxNode node) =>
        node is LambdaExpressionSyntax lambda
            ? Body(lambda)
            : node.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(m => m.Identifier.Text is "BuildModel" or "BuildTargetModel")
                .SelectMany(m => m.Body?.Statements ?? default)
                .OfType<ExpressionStatementSyntax>();

    private static IEnumerable<ExpressionStatementSyntax> Body(LambdaExpressionSyntax lambda) =>
        lambda.Block is { } block
            ? block.Statements.OfType<ExpressionStatementSyntax>()
            : lambda.ExpressionBody is { } expression
                ? [SyntaxFactory.ExpressionStatement(expression)]
                : [];

    private static string? Parameter(LambdaExpressionSyntax lambda) => lambda switch
    {
        SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.Text,
        ParenthesizedLambdaExpressionSyntax paren =>
            paren.ParameterList.Parameters.FirstOrDefault()?.Identifier.Text,
        _ => null,
    };

    /// <summary>The last identifier in a possibly-qualified type name: <c>A.B.C</c> gives <c>C</c>.</summary>
    private static string Name(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        QualifiedNameSyntax qualified => Name(qualified.Right),
        GenericNameSyntax generic => generic.Identifier.Text,
        _ => type.ToString(),
    };

    /// <summary>
    /// One flattened fluent chain, innermost call first, plus the identifier it started from. The
    /// receiver is what says which scope a statement belongs to: <c>modelBuilder</c> is the root,
    /// <c>b</c> is the entity being configured, <c>b1</c> a nested owned type. Anything else is
    /// something we do not understand and skip.
    /// </summary>
    private sealed class Chain
    {
        private Chain(string? receiver, List<Call> calls)
        {
            Receiver = receiver;
            Calls = calls;
        }

        public string? Receiver { get; }

        public IReadOnlyList<Call> Calls { get; }

        public static Chain? From(ExpressionSyntax expression)
        {
            var calls = new List<Call>();
            var current = expression;

            while (current is InvocationExpressionSyntax invocation)
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax member)
                {
                    return null;
                }

                calls.Add(new Call(
                    member.Name is GenericNameSyntax generic
                        ? generic.Identifier.Text
                        : member.Name.Identifier.Text,
                    member.Name is GenericNameSyntax g
                        ? g.TypeArgumentList.Arguments.FirstOrDefault()?.ToString()
                        : null,
                    invocation.ArgumentList.Arguments));

                current = member.Expression;
            }

            if (calls.Count == 0 || current is not IdentifierNameSyntax identifier)
            {
                return null;
            }

            calls.Reverse();
            return new Chain(identifier.Identifier.Text, calls);
        }
    }

    /// <summary>One call in a chain, with the argument readers the callers need.</summary>
    private sealed class Call(
        string name, string? typeArgument, SeparatedSyntaxList<ArgumentSyntax> arguments)
    {
        public string Name { get; } = name;

        /// <summary>The <c>T</c> in <c>Property&lt;T&gt;</c>, verbatim, so <c>int?</c> stays nullable.</summary>
        public string? TypeArgument { get; } = typeArgument;

        /// <summary>The lambda argument, for <c>Entity</c>, <c>OwnsOne</c> and <c>OwnsMany</c>.</summary>
        public LambdaExpressionSyntax? Lambda { get; } = arguments
            .Select(a => a.Expression)
            .OfType<LambdaExpressionSyntax>()
            .FirstOrDefault();

        /// <summary>
        /// The literal at <paramref name="index"/>, or null when it is absent, is the literal
        /// <c>null</c>, or is something other than a literal. EF writes <c>(string)null</c> in
        /// places, so casts are unwrapped rather than treated as unreadable.
        /// </summary>
        public string? Text(int index) =>
            index < arguments.Count ? Literal(arguments[index].Expression) : null;

        /// <summary>Every string literal argument, skipping lambdas, nulls and anything else.</summary>
        public IReadOnlyList<string> Texts() =>
        [
            .. arguments
                .Select(a => Literal(a.Expression))
                .Where(t => t is not null)
                .Select(t => t!)
        ];

        public int? Int(int index) =>
            int.TryParse(Text(index), out var value) ? value : null;

        /// <summary>
        /// A flag argument. <c>IsRequired()</c> and <c>IsUnique()</c> take no argument and mean true;
        /// the explicit <c>IsRequired(false)</c> form has to mean false.
        /// </summary>
        public bool Bool(int index) => Text(index) switch
        {
            null => true,
            "false" => false,
            _ => true,
        };

        /// <summary>
        /// The trailing identifier of a member access argument: <c>DeleteBehavior.Cascade</c> gives
        /// <c>Cascade</c>. Deliberately not mapped to an enum — an unknown future value should
        /// display as itself rather than fall back to a default that means something else.
        /// </summary>
        public string? Member(int index) =>
            index < arguments.Count
            && arguments[index].Expression is MemberAccessExpressionSyntax member
                ? member.Name.Identifier.Text
                : null;

        private static string? Literal(ExpressionSyntax expression) => expression switch
        {
            CastExpressionSyntax cast => Literal(cast.Expression),
            ParenthesizedExpressionSyntax paren => Literal(paren.Expression),
            LiteralExpressionSyntax literal =>
                literal.IsKind(SyntaxKind.NullLiteralExpression)
                    ? null
                    : literal.Token.ValueText,
            _ => null,
        };
    }

    /// <summary>Accumulates entities and relationships while the tree is walked.</summary>
    private sealed class ParseState
    {
        private readonly Dictionary<string, EntityBuilder> _byName = new(StringComparer.Ordinal);

        public string? EfVersion { get; set; }

        /// <summary>In the order EF wrote them, which is alphabetical and therefore stable.</summary>
        public List<EntityBuilder> Entities { get; } = [];

        public List<DiagramRelationship> Relationships { get; } = [];

        public EntityBuilder GetOrAdd(string name)
        {
            if (_byName.TryGetValue(name, out var existing))
            {
                return existing;
            }

            var entity = new EntityBuilder(name);
            _byName[name] = entity;
            Entities.Add(entity);
            return entity;
        }
    }

    /// <summary>
    /// The mutable half of an entity, because EF spreads one entity over several blocks and a record
    /// would mean rebuilding it on every call.
    /// </summary>
    private sealed class EntityBuilder(string name)
    {
        private readonly List<DiagramProperty> _properties = [];

        public string Name { get; } = name;

        public string? Table { get; set; }

        public string? Schema { get; set; }

        public string? BaseType { get; set; }

        public string? DiscriminatorProperty { get; set; }

        public string? DiscriminatorValue { get; set; }

        public bool IsOwned { get; set; }

        public string? OwnerName { get; set; }

        public IReadOnlyList<string> Keys { get; set; } = [];

        public List<IReadOnlyList<string>> AlternateKeys { get; } = [];

        public List<DiagramIndex> Indexes { get; } = [];

        public List<string> Navigations { get; } = [];

        /// <summary>
        /// Last write wins for a property that appears twice. EF does not normally repeat one, but a
        /// merged pair of blocks could, and a duplicated row in a diagram is worse than a lost detail.
        /// </summary>
        public void SetProperty(DiagramProperty property)
        {
            var index = _properties.FindIndex(p => p.Name == property.Name);
            if (index < 0)
            {
                _properties.Add(property);
            }
            else
            {
                _properties[index] = property;
            }
        }

        public DiagramEntity Build() => new(
            Name,
            Table,
            Schema,
            BaseType,
            DiscriminatorProperty,
            DiscriminatorValue,
            IsOwned,
            IsImplicitJoin: false,
            OwnerName,
            [.. _properties],
            [.. Indexes],
            Keys,
            [.. AlternateKeys],
            [.. Navigations]);
    }
}
