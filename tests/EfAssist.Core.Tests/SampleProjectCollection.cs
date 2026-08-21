namespace EfAssist.Core.Tests;

/// <summary>
/// Test classes that shell out to <c>dotnet ef</c> against <c>samples/SampleEfApp</c>. They share one
/// project, one build output and one SQLite file, so running them in parallel produces failures that
/// look like product bugs but are really two builds racing.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class SampleProjectCollection
{
    public const string Name = "SampleProject";
}
