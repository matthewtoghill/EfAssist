using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using EfAssist.Core.Diagrams;
using CoreTextAlignment = EfAssist.Core.Diagrams.TextAlignment;

namespace EfAssist.App.Views;

/// <summary>
/// Draws a <see cref="DiagramScene"/>, and owns pan, zoom, selection and node dragging.
/// </summary>
/// <remarks>
/// <para>
/// A custom-drawn control rather than real controls on a Canvas. The scene is a flat list of
/// primitives so that the SVG, PNG and PDF exports can replay the same list — building the diagram
/// out of Borders and TextBlocks would give free hit-testing but leave every export re-deriving the
/// diagram from scratch, and then what is exported is not quite what is on screen. Hit-testing a
/// list of rectangles is a loop.
/// </para>
/// <para>
/// Pan and zoom are hand-rolled. <c>Avalonia.Controls.PanAndZoom</c> is the obvious dependency and
/// the wrong one: it is marked deprecated on NuGet, its latest release requires Avalonia 11, and this
/// app is on 12.1.1.
/// </para>
/// </remarks>
public class DiagramSurface : Control
{
    public static readonly StyledProperty<DiagramScene?> SceneProperty =
        AvaloniaProperty.Register<DiagramSurface, DiagramScene?>(nameof(Scene));

    /// <summary>When false the surface pans and zooms only, and nodes cannot be moved.</summary>
    public static readonly StyledProperty<bool> IsUnlockedProperty =
        AvaloniaProperty.Register<DiagramSurface, bool>(nameof(IsUnlocked));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<DiagramSurface, double>(nameof(Zoom), 1.0);

    public static readonly StyledProperty<FontFamily> RowFontFamilyProperty =
        AvaloniaProperty.Register<DiagramSurface, FontFamily>(
            nameof(RowFontFamily), new FontFamily("Consolas,Menlo,monospace"));

    private const double MinZoom = 0.15;
    private const double MaxZoom = 4.0;

    /// <summary>Pointer travel before a press counts as a drag rather than a click.</summary>
    private const double DragThreshold = 3;

    private Point _pan;

    private Point _pointerDownAt;
    private Point _panAtPointerDown;
    private bool _panning;

    private string? _draggingEntity;
    private Point _dragOffset;
    private bool _dragMoved;

    /// <summary>A fit was asked for before there was a viewport to fit into. See <see cref="FitToWindow"/>.</summary>
    private bool _fitPending;

    static DiagramSurface()
    {
        AffectsRender<DiagramSurface>(SceneProperty, ZoomProperty);
        FocusableProperty.OverrideDefaultValue<DiagramSurface>(true);
    }

    /// <summary>Raised when a node is clicked, or when the background is clicked with null.</summary>
    public event EventHandler<string?>? SelectionRequested;

    /// <summary>Raised when a node has been dragged to a new scene-space position.</summary>
    public event EventHandler<(string Entity, DiagramPoint Position)>? NodeMoved;

    public DiagramScene? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public bool IsUnlocked
    {
        get => GetValue(IsUnlockedProperty);
        set => SetValue(IsUnlockedProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, Math.Clamp(value, MinZoom, MaxZoom));
    }

    public FontFamily RowFontFamily
    {
        get => GetValue(RowFontFamilyProperty);
        set => SetValue(RowFontFamilyProperty, value);
    }

    // ---- View commands ----

    /// <summary>Scales and centres so the whole diagram is visible, or resets when it is empty.</summary>
    /// <remarks>
    /// A fit asked for while the surface has no size yet — which is the normal case, because a
    /// diagram is usually loaded before its tab has ever been shown — is remembered and carried out
    /// as soon as there is a viewport to fit to. Without that, the first look at a large diagram is
    /// its top-left corner at 1:1.
    /// </remarks>
    public void FitToWindow()
    {
        var size = Scene?.Size;

        if (size is { Width: > 0, Height: > 0 } && (Bounds.Width <= 0 || Bounds.Height <= 0))
        {
            _fitPending = true;
            return;
        }

        _fitPending = false;

        if (size is not { Width: > 0, Height: > 0 } || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            ResetView();
            return;
        }

        var scale = Math.Min(Bounds.Width / size.Value.Width, Bounds.Height / size.Value.Height);

        // Never zoom past 1 to fit: a two-entity model blown up to fill the window looks broken.
        Zoom = Math.Clamp(scale * 0.95, MinZoom, 1.0);

        _pan = new Point(
            (Bounds.Width - (size.Value.Width * Zoom)) / 2,
            (Bounds.Height - (size.Value.Height * Zoom)) / 2);

        InvalidateVisual();
    }

    public void ResetView()
    {
        Zoom = 1;
        _pan = default;
        InvalidateVisual();
    }

    public void ZoomBy(double factor) => ZoomAbout(factor, Bounds.Center);

    /// <summary>Pans so an entity sits in the middle of the viewport, without changing the zoom.</summary>
    public void CentreOn(string entityName)
    {
        if (Scene is null || !Scene.Nodes.TryGetValue(entityName, out var bounds))
        {
            return;
        }

        _pan = new Point(
            (Bounds.Width / 2) - (bounds.CentreX * Zoom),
            (Bounds.Height / 2) - (bounds.CentreY * Zoom));

        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        if (_fitPending)
        {
            FitToWindow();
        }
    }

    // ---- Input ----

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // About the cursor rather than the centre, so zooming in on a node keeps that node under the
        // pointer instead of sliding it off screen.
        ZoomAbout(e.Delta.Y > 0 ? 1.15 : 1 / 1.15, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pointerDownAt = point.Position;
        _dragMoved = false;

        var hit = NodeAt(ToScene(point.Position));

        if (IsUnlocked && hit is not null)
        {
            _draggingEntity = hit;
            var bounds = Scene!.Nodes[hit];
            var scenePoint = ToScene(point.Position);
            _dragOffset = new Point(scenePoint.X - bounds.X, scenePoint.Y - bounds.Y);
        }
        else
        {
            _panning = true;
            _panAtPointerDown = _pan;
        }

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var position = e.GetPosition(this);
        var delta = position - _pointerDownAt;

        if (!_dragMoved && Math.Abs(delta.X) + Math.Abs(delta.Y) > DragThreshold)
        {
            _dragMoved = true;
        }

        if (_draggingEntity is not null && _dragMoved)
        {
            var scenePoint = ToScene(position);
            NodeMoved?.Invoke(this, (
                _draggingEntity,
                new DiagramPoint(scenePoint.X - _dragOffset.X, scenePoint.Y - _dragOffset.Y)));
        }
        else if (_panning)
        {
            _pan = _panAtPointerDown + delta;
            InvalidateVisual();
        }
        else
        {
            Cursor = new Cursor(
                IsUnlocked && NodeAt(ToScene(position)) is not null
                    ? StandardCursorType.SizeAll
                    : StandardCursorType.Arrow);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        // A press that never moved is a click, whether or not the node was draggable. Selecting on
        // press instead would make every drag also change the selection.
        if (!_dragMoved)
        {
            SelectionRequested?.Invoke(this, NodeAt(ToScene(e.GetPosition(this))));
        }

        _draggingEntity = null;
        _panning = false;
        e.Pointer.Capture(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        const double step = 40;

        switch (e.Key)
        {
            case Key.Add or Key.OemPlus:
                ZoomBy(1.15);
                break;
            case Key.Subtract or Key.OemMinus:
                ZoomBy(1 / 1.15);
                break;
            case Key.D0 when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                ResetView();
                break;
            case Key.F:
                FitToWindow();
                break;
            case Key.Escape:
                SelectionRequested?.Invoke(this, null);
                break;
            case Key.Left:
                Nudge(step, 0);
                break;
            case Key.Right:
                Nudge(-step, 0);
                break;
            case Key.Up:
                Nudge(0, step);
                break;
            case Key.Down:
                Nudge(0, -step);
                break;
            default:
                return;
        }

        e.Handled = true;

        void Nudge(double dx, double dy)
        {
            _pan = new Point(_pan.X + dx, _pan.Y + dy);
            InvalidateVisual();
        }
    }

    // ---- Rendering ----

    public override void Render(DrawingContext context)
    {
        var theme = DiagramTheme.For(this);
        context.FillRectangle(theme.Surface, new Rect(Bounds.Size));

        var scene = Scene;
        if (scene is null || scene.IsEmpty)
        {
            return;
        }

        // Clip first: a node dragged past the edge must not paint over the surrounding chrome.
        using var clip = context.PushClip(new Rect(Bounds.Size));
        using var transform = context.PushTransform(
            Matrix.CreateScale(Zoom, Zoom) * Matrix.CreateTranslation(_pan.X, _pan.Y));

        foreach (var shape in scene.Shapes)
        {
            Draw(context, shape, theme);
        }
    }

    private void Draw(DrawingContext context, DiagramShape shape, DiagramTheme theme)
    {
        switch (shape)
        {
            case RectShape rect:
                context.DrawRectangle(
                    theme.Brush(rect.Fill),
                    rect.Border is null
                        ? null
                        : new Pen(theme.Brush(rect.Border.Value), rect.BorderThickness),
                    ToRect(rect.Bounds),
                    rect.CornerRadius,
                    rect.CornerRadius);
                break;

            case PolylineShape line when line.Points.Count > 1:
                var pen = new Pen(theme.Brush(line.Role), line.Thickness)
                {
                    DashStyle = line.Dashed ? new DashStyle([4, 3], 0) : null,
                };

                for (var i = 1; i < line.Points.Count; i++)
                {
                    context.DrawLine(pen, ToPoint(line.Points[i - 1]), ToPoint(line.Points[i]));
                }

                break;

            case TextShape text:
                DrawText(context, text, theme);
                break;
        }
    }

    private void DrawText(DrawingContext context, TextShape text, DiagramTheme theme)
    {
        var formatted = new FormattedText(
            text.Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(
                text.Monospace ? RowFontFamily : TextElement.GetFontFamily(this),
                weight: text.Bold ? FontWeight.SemiBold : FontWeight.Normal),
            text.FontSize,
            theme.Brush(text.Role));

        if (!double.IsPositiveInfinity(text.MaxWidth))
        {
            // Ellipsise rather than clip, so a truncated column type still reads as truncated.
            formatted.MaxTextWidth = Math.Max(1, text.MaxWidth);
            formatted.Trimming = TextTrimming.CharacterEllipsis;
            formatted.MaxTextHeight = text.FontSize * 1.6;
        }

        var x = text.Alignment == CoreTextAlignment.Right
            ? text.At.X - formatted.Width
            : text.At.X;

        context.DrawText(formatted, new Point(x, text.At.Y));
    }

    // ---- Coordinates ----

    private Point ToScene(Point screen) =>
        new((screen.X - _pan.X) / Zoom, (screen.Y - _pan.Y) / Zoom);

    private static Point ToPoint(DiagramPoint point) => new(point.X, point.Y);

    private static Rect ToRect(DiagramRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>
    /// The node under a scene-space point. Reversed, so the last one drawn — the one visually on top
    /// when two overlap after dragging — is the one that gets picked.
    /// </summary>
    private string? NodeAt(Point point)
    {
        if (Scene is null)
        {
            return null;
        }

        var target = new DiagramPoint(point.X, point.Y);
        return Scene.Nodes.LastOrDefault(n => n.Value.Contains(target)).Key;
    }

    private void ZoomAbout(double factor, Point anchor)
    {
        var before = ToScene(anchor);
        Zoom *= factor;
        var after = ToScene(anchor);

        _pan = new Point(
            _pan.X + ((after.X - before.X) * Zoom),
            _pan.Y + ((after.Y - before.Y) * Zoom));

        InvalidateVisual();
    }
}
