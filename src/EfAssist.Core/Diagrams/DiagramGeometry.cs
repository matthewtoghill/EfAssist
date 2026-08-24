namespace EfAssist.Core.Diagrams;

/// <summary>
/// The geometry primitives layout and the scene are expressed in.
/// </summary>
/// <remarks>
/// Deliberately not Avalonia's <c>Point</c>, <c>Size</c> and <c>Rect</c>. Layout is a pure function
/// in Core, the SVG writer is a text writer in Core, and neither should need a UI framework to exist.
/// The renderer converts at the boundary, which is one line each.
/// </remarks>
public readonly record struct DiagramPoint(double X, double Y)
{
    public DiagramPoint Offset(double dx, double dy) => new(X + dx, Y + dy);
}

public readonly record struct DiagramSize(double Width, double Height);

public readonly record struct DiagramRect(double X, double Y, double Width, double Height)
{
    public double Left => X;

    public double Top => Y;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CentreY => Y + (Height / 2);

    public double CentreX => X + (Width / 2);

    public DiagramPoint TopLeft => new(X, Y);

    public bool Contains(DiagramPoint point) =>
        point.X >= X && point.X <= Right && point.Y >= Y && point.Y <= Bottom;

    public DiagramRect WithPosition(DiagramPoint position) =>
        new(position.X, position.Y, Width, Height);

    public DiagramRect Inflate(double amount) =>
        new(X - amount, Y - amount, Width + (2 * amount), Height + (2 * amount));
}
