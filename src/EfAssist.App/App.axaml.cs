using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using EfAssist.App.ViewModels;
using EfAssist.App.Views;

namespace EfAssist.App;

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
            // Before the window exists: the only moment Fluent will accept a palette, and it also
            // means a dark-theme user never sees a white flash.
            viewModel.Appearance.Initialise();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // Probing the tooling takes about a second; don't hold up first paint for it. The
            // update check goes out at the same time and stays silent unless it finds something.
            desktop.MainWindow.Opened += async (_, _) =>
            {
                if (viewModel.CheckForUpdatesOnLaunch)
                {
                    _ = viewModel.Update.CheckOnStartupAsync();
                }
                await viewModel.InitialiseAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
