using Avalonia.Controls;
using EfAssist.App.ViewModels;

namespace EfAssist.App.Views;

/// <summary>
/// The keyboard shortcut reference. Reads <see cref="Shortcuts.Groups"/> rather than listing the
/// gestures in markup, so the sheet and the bindings have one place to disagree instead of two.
/// </summary>
public partial class ShortcutsWindow : Window
{
    public ShortcutsWindow()
    {
        InitializeComponent();
        Groups.ItemsSource = Shortcuts.Groups;

        CloseButton.Click += (_, _) => Close();
    }
}
