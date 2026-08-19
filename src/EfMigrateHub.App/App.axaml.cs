using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using EfMigrateHub.Core;
using EfMigrateHub.App.ViewModels;
using EfMigrateHub.App.Views;

namespace EfMigrateHub.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel();
            // Before the window exists, so a dark-theme user never sees a white flash.
            ApplyTheme(viewModel.Theme);
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // Probing the tooling takes about a second; don't hold up first paint for it.
            desktop.MainWindow.Opened += async (_, _) => await viewModel.InitialiseAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Points Avalonia at the chosen variant. Does nothing when there is no application - the view
    /// models are constructed directly in tests and in the XAML previewer, neither of which has one.
    /// </summary>
    public static void ApplyTheme(AppTheme theme)
    {
        if (Current is null)
        {
            return;
        }

        Current.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            // Default, not a guess at what the OS wants: Avalonia already tracks that, including
            // changes made while the app is running.
            _ => ThemeVariant.Default,
        };
    }
}
