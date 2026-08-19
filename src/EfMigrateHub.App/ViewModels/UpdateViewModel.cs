using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EfMigrateHub.App.Updates;

namespace EfMigrateHub.App.ViewModels;

/// <summary>What the update check last did. Drives both the banner and the home page button.</summary>
public enum UpdateState
{
    /// <summary>Never checked, or the result was dismissed.</summary>
    Idle,

    Checking,

    UpToDate,

    Available,

    Downloading,

    /// <summary>The check or the download failed. <see cref="UpdateViewModel.Message"/> says how.</summary>
    Failed,
}

/// <summary>
/// The in-app updater. Checks once, quietly, shortly after launch, and on demand from the home page.
/// A failed check is never allowed to interrupt anything — the app's whole job works offline.
/// </summary>
public partial class UpdateViewModel : ObservableObject
{
    private readonly IAppUpdater _updater;

    public UpdateViewModel() : this(new VelopackUpdater())
    {
    }

    public UpdateViewModel(IAppUpdater updater) => _updater = updater;

    public string CurrentVersion => _updater.CurrentVersion;

    /// <summary>False for a development or portable run: there is nothing to update in place.</summary>
    public bool CanUpdate => _updater.CanUpdate;

    [ObservableProperty]
    private UpdateState _state = UpdateState.Idle;

    /// <summary>Plain-language status for the home page. Empty when there is nothing to say.</summary>
    [ObservableProperty]
    private string _message = "";

    /// <summary>The version offered, once one has been found.</summary>
    [ObservableProperty]
    private string? _availableVersion;

    /// <summary>
    /// The banner is dismissible so it cannot sit over the workspace forever. Dismissing hides it
    /// for the session; the next launch checks again.
    /// </summary>
    [ObservableProperty]
    private bool _dismissed;

    public bool IsUpdateAvailable => State is UpdateState.Available or UpdateState.Downloading;

    public bool ShowBanner => IsUpdateAvailable && !Dismissed;

    /// <summary>
    /// The home page's fallback offer. The banner is dismissible, so without this a dismissed update
    /// would have no way back short of reopening settings and checking again.
    /// </summary>
    public bool ShowHomeOffer => IsUpdateAvailable && Dismissed;

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading;

    /// <summary>
    /// Runs on launch. Silent by design: no message when up to date, no error when the network is
    /// down. Only a found update surfaces, as the banner.
    /// </summary>
    public async Task CheckOnStartupAsync()
    {
        if (!CanUpdate || IsBusy)
        {
            return;
        }

        try
        {
            State = UpdateState.Checking;
            var version = await _updater.CheckAsync();
            if (version is null)
            {
                State = UpdateState.Idle;
                return;
            }

            AvailableVersion = version;
            Message = $"Version {version} is available.";
            State = UpdateState.Available;
        }
        catch
        {
            // A background check that cannot reach GitHub says nothing. The manual button reports.
            State = UpdateState.Idle;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        try
        {
            State = UpdateState.Checking;
            Message = "Checking for updates…";
            Dismissed = false;

            var version = await _updater.CheckAsync();
            if (version is null)
            {
                Message = $"EfMigrateHub {CurrentVersion} is up to date.";
                State = UpdateState.UpToDate;
                return;
            }

            AvailableVersion = version;
            Message = $"Version {version} is available.";
            State = UpdateState.Available;
        }
        catch (Exception ex)
        {
            Message = $"Could not check for updates: {FirstLine(ex.Message)}";
            State = UpdateState.Failed;
        }
    }

    private bool CanCheck() => CanUpdate && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task UpdateNowAsync()
    {
        try
        {
            State = UpdateState.Downloading;
            Message = $"Downloading version {AvailableVersion}…";

            // Restarts the process on success, so nothing after this runs in the normal case.
            await _updater.ApplyAndRestartAsync();
        }
        catch (Exception ex)
        {
            Message = $"Could not install the update: {FirstLine(ex.Message)}";
            State = UpdateState.Failed;
        }
    }

    private bool CanApply() => State == UpdateState.Available;

    [RelayCommand]
    private void Dismiss() => Dismissed = true;

    partial void OnStateChanged(UpdateState value)
    {
        OnPropertyChanged(nameof(IsUpdateAvailable));
        OnPropertyChanged(nameof(ShowBanner));
        OnPropertyChanged(nameof(ShowHomeOffer));
        OnPropertyChanged(nameof(IsBusy));
        CheckCommand.NotifyCanExecuteChanged();
        UpdateNowCommand.NotifyCanExecuteChanged();
    }

    partial void OnDismissedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBanner));
        OnPropertyChanged(nameof(ShowHomeOffer));
    }

    private static string FirstLine(string text)
    {
        var newline = text.IndexOf('\n');
        return newline < 0 ? text : text[..newline].TrimEnd('\r');
    }
}
