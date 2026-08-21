using Avalonia.Controls;

namespace EfAssist.App.Views;

/// <summary>
/// Theme, colours, font sizes, dotnet-ef and app updates. Modal, and shares the shell's view model
/// rather than owning its own, so the Tools and Updates sections drive exactly the same commands the
/// rest of the app does.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        // Everything here applies as it is changed, so Close is the only action.
        CloseButton.Click += (_, _) => Close();
    }
}
