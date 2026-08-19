using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EfMigrateHub.App.ViewModels;

namespace EfMigrateHub.App.Views;

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
}
