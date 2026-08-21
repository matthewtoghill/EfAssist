using System;
using System.Threading.Tasks;
using EfAssist.App.Updates;
using EfAssist.App.ViewModels;

namespace EfAssist.Core.Tests;

/// <summary>
/// A stand-in for Velopack. Nothing reaches GitHub and nothing needs an installed application, so
/// the states the banner and the home page render can all be produced deterministically.
/// </summary>
file sealed class FakeUpdater : IAppUpdater
{
    public string CurrentVersion { get; init; } = "1.0.0";

    public bool CanUpdate { get; init; } = true;

    /// <summary>The version <see cref="CheckAsync"/> reports. Null means up to date.</summary>
    public string? Available { get; init; }

    public Exception? CheckThrows { get; init; }

    public Exception? ApplyThrows { get; init; }

    public int Checks { get; private set; }

    public int Applies { get; private set; }

    public Task<string?> CheckAsync()
    {
        Checks++;
        return CheckThrows is not null
            ? Task.FromException<string?>(CheckThrows)
            : Task.FromResult(Available);
    }

    public Task ApplyAndRestartAsync()
    {
        Applies++;
        return ApplyThrows is not null ? Task.FromException(ApplyThrows) : Task.CompletedTask;
    }
}

public class UpdateViewModelTests
{
    [Fact]
    public async Task Manual_check_reports_up_to_date()
    {
        var vm = new UpdateViewModel(new FakeUpdater { CurrentVersion = "1.2.3" });

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.UpToDate, vm.State);
        Assert.Contains("1.2.3", vm.Message);
        Assert.False(vm.ShowBanner);
    }

    [Fact]
    public async Task Manual_check_surfaces_an_available_version()
    {
        var vm = new UpdateViewModel(new FakeUpdater { Available = "1.3.0" });

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.Available, vm.State);
        Assert.Equal("1.3.0", vm.AvailableVersion);
        Assert.True(vm.ShowBanner);
        Assert.True(vm.UpdateNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_failed_manual_check_says_so_and_offers_no_update()
    {
        var vm = new UpdateViewModel(new FakeUpdater { CheckThrows = new InvalidOperationException("no network") });

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.Failed, vm.State);
        Assert.Contains("no network", vm.Message);
        Assert.False(vm.ShowBanner);
        Assert.False(vm.UpdateNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_failed_startup_check_is_silent()
    {
        var vm = new UpdateViewModel(new FakeUpdater { CheckThrows = new InvalidOperationException("no network") });

        await vm.CheckOnStartupAsync();

        Assert.Equal(UpdateState.Idle, vm.State);
        Assert.Equal("", vm.Message);
        Assert.False(vm.ShowBanner);
    }

    [Fact]
    public async Task A_startup_check_that_finds_nothing_says_nothing()
    {
        var vm = new UpdateViewModel(new FakeUpdater());

        await vm.CheckOnStartupAsync();

        Assert.Equal(UpdateState.Idle, vm.State);
        Assert.Equal("", vm.Message);
    }

    [Fact]
    public async Task A_startup_check_that_finds_an_update_shows_the_banner()
    {
        var vm = new UpdateViewModel(new FakeUpdater { Available = "2.0.0" });

        await vm.CheckOnStartupAsync();

        Assert.Equal(UpdateState.Available, vm.State);
        Assert.True(vm.ShowBanner);
    }

    [Fact]
    public async Task An_uninstalled_build_never_checks()
    {
        var updater = new FakeUpdater { CanUpdate = false, Available = "2.0.0" };
        var vm = new UpdateViewModel(updater);

        await vm.CheckOnStartupAsync();

        Assert.Equal(0, updater.Checks);
        Assert.False(vm.CheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task Dismissing_hides_the_banner_without_forgetting_the_update()
    {
        var vm = new UpdateViewModel(new FakeUpdater { Available = "1.3.0" });
        await vm.CheckOnStartupAsync();

        vm.DismissCommand.Execute(null);

        Assert.False(vm.ShowBanner);
        Assert.True(vm.IsUpdateAvailable);
        Assert.True(vm.UpdateNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task Update_now_applies_the_update_found_by_the_check()
    {
        var updater = new FakeUpdater { Available = "1.3.0" };
        var vm = new UpdateViewModel(updater);
        await vm.CheckCommand.ExecuteAsync(null);

        await vm.UpdateNowCommand.ExecuteAsync(null);

        Assert.Equal(1, updater.Applies);
    }

    [Fact]
    public async Task A_failed_download_reports_and_does_not_leave_the_banner_claiming_progress()
    {
        var updater = new FakeUpdater { Available = "1.3.0", ApplyThrows = new InvalidOperationException("disk full") };
        var vm = new UpdateViewModel(updater);
        await vm.CheckCommand.ExecuteAsync(null);

        await vm.UpdateNowCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.Failed, vm.State);
        Assert.Contains("disk full", vm.Message);
        Assert.False(vm.ShowBanner);
    }

    [Fact]
    public void The_current_version_comes_from_the_updater()
    {
        var vm = new UpdateViewModel(new FakeUpdater { CurrentVersion = "9.9.9" });

        Assert.Equal("9.9.9", vm.CurrentVersion);
    }
}
