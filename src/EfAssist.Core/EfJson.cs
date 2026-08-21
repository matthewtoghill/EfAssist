using System.Text.Json;

namespace EfAssist.Core;

/// <summary>Whether a migration has been applied to the database.</summary>
public enum MigrationState
{
    /// <summary>EF could not tell us — <c>--no-connect</c>, or it could not reach the database.
    /// Distinct from <see cref="Pending"/> on purpose: claiming "pending" here would be a lie.</summary>
    Unknown,
    Pending,
    Applied,
}

/// <summary>One entry from <c>dotnet ef migrations list --json</c>.</summary>
public sealed record MigrationInfo(string Id, string Name, string SafeName, bool? Applied)
{
    public MigrationState State => Applied switch
    {
        true => MigrationState.Applied,
        false => MigrationState.Pending,
        null => MigrationState.Unknown,
    };
}

/// <summary>One entry from <c>dotnet ef dbcontext list --json</c>.</summary>
public sealed record DbContextRef(string FullName, string SafeName, string Name, string AssemblyQualifiedName);

/// <summary>The output of <c>dotnet ef dbcontext info --json</c>.</summary>
public sealed record DbContextDetails(
    string Type,
    string ProviderName,
    string DatabaseName,
    string DataSource,
    string Options)
{
    /// <summary>
    /// Providers whose <c>databaseName</c> is a fixed constant rather than the actual database —
    /// SQLite always reports "main", which is useless as a confirmation prompt.
    /// </summary>
    private static readonly HashSet<string> GenericDatabaseNames =
        new(StringComparer.OrdinalIgnoreCase) { "main", "" };

    /// <summary>What to make the user type to confirm a destructive operation.</summary>
    public string ConfirmationName
    {
        get
        {
            // Deserialized from JSON, so treat every field as possibly absent.
            var name = DatabaseName ?? "";
            return GenericDatabaseNames.Contains(name) ? DataSource ?? "" : name;
        }
    }

    /// <summary>
    /// Whether <c>migrations script --idempotent</c> works for this provider. SQLite throws
    /// NotSupportedException. Unknown providers get the benefit of the doubt — better to attempt it
    /// and surface a clear error than to grey out a button that would have worked.
    /// </summary>
    public bool SupportsIdempotentScripts =>
        !(ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>Deserializes the <c>data:</c> payload of an <see cref="EfResult"/>.</summary>
public static class EfJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Returns null when there is no payload or it will not parse. Callers should surface
    /// <see cref="EfResult.Diagnostics"/> in that case rather than guessing — a null here means EF
    /// gave us something we do not understand, which is exactly when the raw output matters.
    /// </summary>
    public static T? Deserialize<T>(EfResult result) where T : class => Deserialize<T>(result.Data);

    public static T? Deserialize<T>(string payload) where T : class
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(payload, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyList<MigrationInfo>? Migrations(EfResult result) =>
        Deserialize<List<MigrationInfo>>(result);

    public static IReadOnlyList<DbContextRef>? Contexts(EfResult result) =>
        Deserialize<List<DbContextRef>>(result);

    public static DbContextDetails? ContextDetails(EfResult result) =>
        Deserialize<DbContextDetails>(result);
}
