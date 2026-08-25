using SampleRichModel;

// Nothing to run. The project exists so `dotnet ef` has a buildable host with a DbContext in it;
// see README.md. Referencing the context keeps the compiler from warning the using is unused.
Console.WriteLine($"{nameof(RichContext)} is a fixture source, not an app.");
