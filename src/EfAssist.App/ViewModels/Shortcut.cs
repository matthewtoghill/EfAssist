using System.Collections.Generic;

namespace EfAssist.App.ViewModels;

/// <summary>One keyboard shortcut, as the reference sheet lists it.</summary>
/// <param name="Gesture">How it is typed, in the spelling a person would say out loud.</param>
/// <param name="Description">What it does.</param>
public sealed record Shortcut(string Gesture, string Description);

/// <summary>A named group of shortcuts on the reference sheet.</summary>
public sealed record ShortcutGroup(string Title, IReadOnlyList<Shortcut> Shortcuts);

/// <summary>
/// The shortcut reference, in one place.
/// </summary>
/// <remarks>
/// The app's shortcuts were previously discoverable only from a tooltip or the source. This is the
/// list the sheet renders, and it is the only list: a binding added to <c>MainWindow.axaml</c>
/// belongs here in the same change, or the sheet quietly starts lying.
/// </remarks>
public static class Shortcuts
{
    public static IReadOnlyList<ShortcutGroup> Groups { get; } =
    [
        new ShortcutGroup("Getting around", [
            new Shortcut("Alt+1", "Migrations"),
            new Shortcut("Alt+2", "Script"),
            new Shortcut("Alt+3", "Diagrams"),
            new Shortcut("Alt+4", "Tools"),
            new Shortcut("F1 or Alt+/", "This list"),
        ]),

        new ShortcutGroup("Panels", [
            new Shortcut("Ctrl+,", "Settings: theme, colours, font size, dotnet-ef and app updates"),
        ]),

        new ShortcutGroup("In the SQL and source viewers", [
            new Shortcut("Ctrl+F", "Find"),
            new Shortcut("F3", "Find again"),
            new Shortcut("Ctrl+C", "Copy the selection"),
        ]),
    ];
}
