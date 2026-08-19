using EfMigrateHub.Core;

namespace EfMigrateHub.Core.Tests;

/// <summary>
/// Loads the captured <c>dotnet ef</c> output in <c>Fixtures/</c> and replays it through the real
/// line parser, so these tests exercise the same code path as a live process.
/// </summary>
internal static class Fixture
{
    public static EfResult Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".txt");
        var lines = File.ReadAllLines(path);

        // The capture script appended the exit code as a trailing "exit=N" line.
        var exitCode = 0;
        var output = new List<OutputLine>();
        foreach (var line in lines)
        {
            if (line.StartsWith("exit=", StringComparison.Ordinal))
            {
                exitCode = int.Parse(line["exit=".Length..]);
                continue;
            }

            output.Add(OutputLine.Parse(line));
        }

        return new EfResult(exitCode, output, "dotnet ef <fixture>", AppContext.BaseDirectory);
    }
}
