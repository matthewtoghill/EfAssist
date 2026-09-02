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
            new Shortcut("Ctrl+1", "Migrations"),
            new Shortcut("Ctrl+2", "Script"),
            new Shortcut("Ctrl+3", "Diagrams"),
            new Shortcut("Ctrl+4", "Tools"),
            new Shortcut("Ctrl+W", "Home: close this workspace"),
            new Shortcut("Ctrl+O", "Open a solution"),
            new Shortcut("Ctrl+Shift+O", "Open a folder containing a solution"),
            new Shortcut("F1 or Ctrl+/", "This list"),
        ]),

        new ShortcutGroup("Actions", [
            new Shortcut("Ctrl+N", "Add a migration, with the name box ready to type in"),
            new Shortcut("Enter", "In the Add migration flyout: add it"),
            new Shortcut("Ctrl+G", "Generate: the script on Script, the diagram on Diagrams"),
            new Shortcut("F5", "Refresh the migration list"),
            new Shortcut("Esc", "Stop the running command"),
        ]),

        new ShortcutGroup("Panels", [
            new Shortcut("Ctrl+'", "Show or hide the output panel"),
            new Shortcut("Ctrl+,", "Settings: theme, colours, font size, dotnet-ef and app updates"),
        ]),

        new ShortcutGroup("In the SQL and source viewers", [
            new Shortcut("Ctrl+F", "Find"),
            new Shortcut("F3", "Find again"),
            new Shortcut("Ctrl+C", "Copy the selection"),
        ]),
    ];
}
