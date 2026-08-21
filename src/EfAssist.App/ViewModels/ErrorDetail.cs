namespace EfAssist.App.ViewModels;

/// <summary>
/// A failure's full output, shown in a dismissible modal. Used where there is no console nearby to
/// read it in — the landing page's dotnet-ef update runs before any workspace, and so before the
/// console, is on screen.
/// </summary>
public sealed record ErrorDetail(string Title, string Message);
