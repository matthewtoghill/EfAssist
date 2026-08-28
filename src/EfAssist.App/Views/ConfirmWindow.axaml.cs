using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EfAssist.App.ViewModels;

namespace EfAssist.App.Views;

/// <summary>
/// One reusable confirmation dialog for every destructive action. Modal, so nothing else can be
/// triggered while the warning is on screen.
/// </summary>
public partial class ConfirmWindow : Window
{
    private readonly ConfirmRequest _request;

    /// <summary>Parameterless constructor exists only for the XAML previewer.</summary>
    public ConfirmWindow() : this(new ConfirmRequest("Confirm", "Are you sure?", "Confirm"))
    {
    }

    public ConfirmWindow(ConfirmRequest request)
    {
        _request = request;
        InitializeComponent();
        DataContext = request;

        ConfirmButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);

        if (request.HasPreview)
        {
            PreviewButton.Click += OnPreviewClickAsync;
        }

        if (request.RequiresTyping)
        {
            // Locked until the typed value matches exactly, so a stray Enter cannot drop a database.
            ConfirmButton.IsEnabled = false;
            TypedValue.TextChanged += OnTypedValueChanged;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Focus the gate if there is one; otherwise Cancel, so Enter and Space are the safe answer.
        if (_request.RequiresTyping)
        {
            TypedValue.Focus();
        }
        else
        {
            CancelButton.Focus();
        }
    }

    private void OnTypedValueChanged(object? sender, TextChangedEventArgs e) =>
        ConfirmButton.IsEnabled = _request.IsSatisfiedBy(TypedValue.Text);

    /// <summary>
    /// Generates the SQL and shows it. Confirming is blocked while that runs — the generation is a
    /// build, and answering a question whose evidence is still being fetched is exactly what this
    /// button exists to prevent. Cancel stays live, because a build that hangs must not trap the user
    /// behind a modal with the output panel's own Cancel unreachable behind it.
    /// </summary>
    private async void OnPreviewClickAsync(object? sender, RoutedEventArgs e)
    {
        PreviewButton.IsEnabled = false;
        ConfirmButton.IsEnabled = false;

        try
        {
            await _request.PreviewAsync!();
        }
        finally
        {
            // The dialog may have been cancelled while this ran; setting properties on a closed
            // window is harmless, and the alternative is tracking a flag for no gain.
            PreviewButton.IsEnabled = true;
            ConfirmButton.IsEnabled = _request.IsSatisfiedBy(TypedValue.Text);
        }
    }
}
