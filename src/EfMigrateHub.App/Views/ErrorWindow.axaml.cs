using Avalonia.Controls;
using EfMigrateHub.App.ViewModels;

namespace EfMigrateHub.App.Views;

/// <summary>Read-only modal for a failure's full output, used where there is no console nearby to show it in.</summary>
public partial class ErrorWindow : Window
{
    /// <summary>Parameterless constructor exists only for the XAML previewer.</summary>
    public ErrorWindow() : this(new ErrorDetail("Error", ""))
    {
    }

    public ErrorWindow(ErrorDetail detail)
    {
        InitializeComponent();
        DataContext = detail;

        CloseButton.Click += (_, _) => Close();
    }
}
