using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace EfAssist.App.Views;

/// <summary>
/// Shows a control's keyboard shortcut as a small badge over it, for as long as the modifier the
/// shortcut starts with is held down.
/// </summary>
/// <remarks>
/// A shortcut only helps if you know it exists, and a tooltip only says so once you have already
/// reached for the mouse. Holding Alt or Ctrl labels every button that gesture can reach, the way
/// Windows labels access keys.
///
/// Mark a control with <c>views:ShortcutHint.Gesture="Alt+1"</c>. The text before the first
/// <c>+</c> is the modifier that reveals it; the rest is what the badge reads. The gesture written
/// here is a caption, not a binding: the accelerator itself still lives in
/// <c>Window.KeyBindings</c>, and <see cref="ViewModels.Shortcuts"/> still lists it for the
/// reference sheet.
/// </remarks>
public static class ShortcutHint
{
    /// <summary>
    /// How long the modifier has to be held before the badges appear. Long enough that typing a
    /// shortcut you already know does not flash the whole set on screen, short enough that holding
    /// the key to ask the question feels answered.
    /// </summary>
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(400);

    /// <summary>The shortcut this control answers to, as Modifier+Key - "Alt+1", "Ctrl+,".</summary>
    public static readonly AttachedProperty<string?> GestureProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Gesture", typeof(ShortcutHint));

    /// <summary>
    /// Which side of the control the badge hangs off. Right for a control the badge can sit on top
    /// of; Left for a zero-width anchor dropped into a gap, where the badge belongs to its right.
    /// </summary>
    public static readonly AttachedProperty<HorizontalAlignment> AlignProperty =
        AvaloniaProperty.RegisterAttached<Control, HorizontalAlignment>(
            "Align", typeof(ShortcutHint), HorizontalAlignment.Right);

    public static void SetGesture(Control control, string? value) => control.SetValue(GestureProperty, value);
    public static string? GetGesture(Control control) => control.GetValue(GestureProperty);
    public static void SetAlign(Control control, HorizontalAlignment value) => control.SetValue(AlignProperty, value);
    public static HorizontalAlignment GetAlign(Control control) => control.GetValue(AlignProperty);

    /// <summary>Every control that has declared a gesture, so a key press can find them all.</summary>
    private static readonly List<Control> Marked = [];

    /// <summary>The controls currently wearing a badge, so they can be undressed again.</summary>
    private static readonly List<Control> Showing = [];

    private static readonly DispatcherTimer Timer = new() { Interval = Hold };

    /// <summary>
    /// The modifier currently held down, whether or not its badges are up yet. Windows auto-repeats
    /// a held key, so this is also what tells a repeat apart from a fresh press - without it the
    /// badges would come back <see cref="Hold"/> after every shortcut typed with Ctrl still down.
    /// </summary>
    private static string? _held;

    static ShortcutHint()
    {
        GestureProperty.Changed.AddClassHandler<Control, string?>((control, e) =>
        {
            Marked.Remove(control);
            if (!string.IsNullOrEmpty(e.NewValue.GetValueOrDefault())) Marked.Add(control);
        });

        Timer.Tick += (_, _) =>
        {
            Timer.Stop();
            Show(_held);
        };
    }

    /// <summary>
    /// Starts watching a window's keyboard for held modifiers. Both routes and handled keys too:
    /// these handlers only watch, and the keys that matter most are exactly the ones something else
    /// has already handled - a shortcut its own KeyBinding just ran, or a keystroke the SQL editor
    /// swallowed.
    /// </summary>
    public static void Attach(Window window)
    {
        const RoutingStrategies both = RoutingStrategies.Tunnel | RoutingStrategies.Bubble;
        window.AddHandler(InputElement.KeyDownEvent, OnKeyDown, both, handledEventsToo: true);
        window.AddHandler(InputElement.KeyUpEvent, OnKeyUp, both, handledEventsToo: true);
        // Alt+Tab is a held Alt that never gets a key-up here, so the badges would be left behind.
        window.Deactivated += (_, _) => Release();
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var modifier = ModifierOf(e.Key);

        // Anything that is not the modifier on its own is the shortcut being typed, or unrelated
        // work. Either way the question the badges answer has been answered - and it stays
        // answered, because the modifier is still recorded as held and its repeats are ignored.
        if (modifier is null)
        {
            Clear();
            return;
        }

        // A repeat of the key already down, rather than a fresh press.
        if (modifier == _held) return;

        Clear();
        _held = modifier;
        Timer.Start();
    }

    private static void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (ModifierOf(e.Key) is not null) Release();
    }

    /// <summary>Takes the badges down and forgets the modifier, so the next press counts as fresh.</summary>
    private static void Release()
    {
        _held = null;
        Clear();
    }

    /// <summary>The gesture prefix a key stands for on its own, or null if it is not a modifier.</summary>
    private static string? ModifierOf(Key key) => key switch
    {
        Key.LeftAlt or Key.RightAlt => "Alt",
        Key.LeftCtrl or Key.RightCtrl => "Ctrl",
        _ => null,
    };

    private static void Show(string? modifier)
    {
        if (modifier is null) return;

        var prefix = modifier + "+";
        foreach (var control in Marked)
        {
            var gesture = GetGesture(control);
            if (gesture is null || !gesture.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            // A control on a screen that is not showing, or one that cannot be pressed, is no
            // answer to "what can I do from here".
            if (!control.IsEffectivelyVisible || !control.IsEffectivelyEnabled) continue;

            AdornerLayer.SetAdorner(control, Badge(control, Label(gesture, prefix.Length)));
            Showing.Add(control);
        }
    }

    /// <summary>Takes the badges down, leaving <see cref="_held"/> alone.</summary>
    private static void Clear()
    {
        Timer.Stop();
        foreach (var control in Showing) AdornerLayer.SetAdorner(control, null);
        Showing.Clear();
    }

    /// <summary>
    /// What the badge reads. A letter or digit stands on its own, because the modifier is already
    /// under a finger. A punctuation key does not: a comma or a backtick at badge size is a speck,
    /// so those spell the whole gesture out.
    /// </summary>
    private static string Label(string gesture, int keyAt)
    {
        var key = gesture[keyAt..];
        return key.Length == 1 && char.IsLetterOrDigit(key[0]) ? key : gesture;
    }

    private static StackPanel Badge(Control host, string text)
    {
        var align = GetAlign(host);

        var badge = new Border
        {
            Background = Brush(host, "AppAccentBrush"),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 0, 4, 2),
            // It sits over the very thing you are about to click.
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush(host, "StateBadgeForegroundBrush"),
            },
        };

        // Wrapped in a horizontal StackPanel, which is the one panel that measures its child with
        // unbounded width. An adorner is arranged into the bounds it adorns, so a badge placed
        // straight into them is measured at the width of a narrow button and its text comes out
        // truncated - and at the width of a zero-width anchor it disappears entirely.
        var slot = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            // Hung off the top corner like a superscript, labelling the control rather than
            // covering the icon or the words on it.
            Margin = align == HorizontalAlignment.Left
                ? new Thickness(2, -7, 0, 0)
                : new Thickness(0, -7, -7, 0),
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { badge },
        };

        // An adorner is clipped to the bounds it adorns, which would cut the overhang off. The
        // layer reads this from the adorner, not from the adorned control.
        AdornerLayer.SetIsClipEnabled(slot, false);
        return slot;
    }

    /// <summary>
    /// Resolves a theme brush through the badged control, so it picks up the light or dark value in
    /// force there. A badge only lives as long as a key is held, so it does not have to follow a
    /// theme change part way through.
    /// </summary>
    private static IBrush? Brush(Control host, string key) =>
        host.TryFindResource(key, host.ActualThemeVariant, out var value) ? value as IBrush : null;
}
