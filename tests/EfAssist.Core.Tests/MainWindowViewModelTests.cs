using System.Collections.Specialized;
using EfAssist.App.ViewModels;
using EfAssist.Core;

namespace EfAssist.Core.Tests;

/// <summary>
/// The shell view model, driven with a fake runner and a temp settings file. Covers the parts that
/// hold real logic: discovery-mode branching, the --no-build retry, context reselection, and
/// per-workspace persistence.
/// </summary>
[Collection(SampleProjectCollection.Name)]
public class MainWindowViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "EfAssistTests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_root, "settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <summary>Routes on the argument list, so one fake serves preflight, sln list and ef commands.</summary>
    private sealed class RoutingRunner : IEfRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        /// <summary>Contexts returned by `dbcontext list`.</summary>
        public string[] ContextNames { get; set; } = ["BlogContext", "AuditContext"];

        /// <summary>When true, a `--no-build` invocation fails and a building one succeeds.</summary>
        public bool NoBuildFails { get; set; }

        public string? EfToolProblem { get; set; }

        /// <summary>When true, every `dbcontext list` fails, however it is invoked.</summary>
        public bool ContextsFail { get; set; }

        /// <summary>When set, `dotnet tool update` fails with this message instead of succeeding.</summary>
        public string? ToolUpdateError { get; set; }

        public Task<EfResult> RunAsync(
            IReadOnlyList<string> args,
            string workingDirectory,
            IProgress<OutputLine>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(args);

            EfResult result;
            if (args is ["--version"])
            {
                result = Raw(0, "10.0.400");
            }
            else if (args is ["ef", "--version"])
            {
                result = EfToolProblem is null
                    ? Raw(0, "Entity Framework Core .NET Command-line Tools", "10.0.10")
                    : new EfResult(1, [new OutputLine(OutputChannel.Error, EfToolProblem)], "fake", ".");
            }
            else if (args is ["sln", _, "list"])
            {
                result = Raw(0, "Project(s)", "----------", @"src\Api\Api.csproj", @"src\Data\Data.csproj");
            }
            else if (args.Contains("dbcontext"))
            {
                result = ContextsFail
                    ? new EfResult(1, [new OutputLine(OutputChannel.Error, "Build failed.")], "fake", ".")
                    : NoBuildFails && args.Contains("--no-build")
                    ? new EfResult(1, [new OutputLine(OutputChannel.Error, "no build found")], "fake", ".")
                    : Data(ContextsJson(ContextNames));
            }
            else if (args.Contains("migrations"))
            {
                result = Data("[]");
            }
            else if (args.Contains("tool"))
            {
                result = ToolUpdateError is null
                    ? Raw(0, "Tool 'dotnet-ef' was successfully updated.")
                    : new EfResult(1, [new OutputLine(OutputChannel.Error, ToolUpdateError)], "fake", ".");
            }
            else
            {
                result = Raw(0);
            }

            foreach (var line in result.Lines)
            {
                progress?.Report(line);
            }

            return Task.FromResult(result);
        }

        private static EfResult Raw(int exitCode, params string[] lines) =>
            new(exitCode, lines.Select(l => new OutputLine(OutputChannel.Raw, l)).ToArray(), "fake", ".");

        private static EfResult Data(string json) =>
            new(
                0,
                json.Split('\n').Select(l => new OutputLine(OutputChannel.Data, l)).ToArray(),
                "fake",
                ".");

        private static string ContextsJson(string[] names) => "[" + string.Join(",", names.Select(n =>
            $$"""
            { "fullName": "Sample.{{n}}", "safeName": "{{n}}", "name": "{{n}}", "assemblyQualifiedName": "Sample.{{n}}, Sample" }
            """)) + "]";
    }

    private const string LibraryCsproj =
        """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";

    private string CreateSolution()
    {
        Write("Thing.slnx", "<Solution />");
        Write(@"src\Api\Api.csproj",
            """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.EntityFrameworkCore.Design" /></ItemGroup></Project>""");
        Write(@"src\Data\Data.csproj", LibraryCsproj);
        Write(@"src\Data\Migrations\20260101000000_InitialCreate.cs", "// migration");
        return Path.Combine(_root, "Thing.slnx");
    }

    /// <summary>The restore output NuGet writes, trimmed to the one entry the versions line reads.</summary>
    private void WriteAssets(string projectDirectory, string efCoreVersion) => Write(
        Path.Combine(projectDirectory, "obj", "project.assets.json"),
        "{\"libraries\":{\"Microsoft.EntityFrameworkCore/" + efCoreVersion + "\": {\"type\":\"package\"}}}");

    private void Write(string relativePath, string contents)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
    }

    private MainWindowViewModel NewViewModel(RoutingRunner runner, AppSettings? settings = null) =>
        new(runner, settings ?? SettingsStore.Load(SettingsPath), SettingsPath)
        {
            // No Avalonia dispatcher to pump here. The fake runner reports inline on the calling
            // thread, so running the action directly keeps the collection single-threaded.
            PostToUiThread = action => action(),
        };

    private static async Task OpenAsync(MainWindowViewModel viewModel, string path)
    {
        viewModel.PickSolutionAsync = () => Task.FromResult<string?>(path);
        await viewModel.OpenSolutionCommand.ExecuteAsync(null);
    }

    private AppSettings SettingsWith(string solution, DiscoveryMode mode)
    {
        var settings = SettingsStore.Load(SettingsPath);
        settings.For(solution).Discovery = mode;
        return settings;
    }

    [Fact]
    public async Task Auto_discovers_contexts_as_soon_as_a_workspace_opens()
    {
        var solution = CreateSolution();
        var runner = new RoutingRunner();
        var viewModel = NewViewModel(runner, SettingsWith(solution, DiscoveryMode.Auto));

        await OpenAsync(viewModel, solution);

        Assert.True(viewModel.HasWorkspace);
        Assert.Equal(["Api", "Data"], viewModel.Projects.Select(p => p.Name));
        Assert.Equal(["BlogContext", "AuditContext"], viewModel.Contexts.Select(c => c.Name));
        Assert.Equal("BlogContext", viewModel.SelectedContext?.Name);
    }

    [Fact]
    public async Task Suggested_projects_are_preselected()
    {
        var solution = CreateSolution();
        var viewModel = NewViewModel(new RoutingRunner());

        await OpenAsync(viewModel, solution);

        Assert.Equal("Api", viewModel.StartupProject?.Name);
        Assert.Equal("Data", viewModel.MigrationsProject?.Name);
    }

    [Fact]
    public async Task Cached_is_the_default_and_discovers_once_when_nothing_is_remembered()
    {
        var solution = CreateSolution();
        var runner = new RoutingRunner();
        var viewModel = NewViewModel(runner);

        Assert.Equal(DiscoveryMode.Cached, viewModel.DiscoveryMode);

        await OpenAsync(viewModel, solution);

        // Nothing remembered yet, so an empty dropdown would be useless — discover this once.
        Assert.Contains(runner.Calls, args => args.Contains("dbcontext"));
        Assert.Equal(["BlogContext", "AuditContext"], viewModel.Contexts.Select(c => c.Name));
    }

    [Fact]
    public async Task Cached_reuses_the_remembered_contexts_and_runs_nothing()
    {
        var solution = CreateSolution();
        await OpenAsync(NewViewModel(new RoutingRunner()), solution);

        // Second open: the list is already known, so this must not build.
        var runner = new RoutingRunner();
        var viewModel = NewViewModel(runner);
        await OpenAsync(viewModel, solution);

        Assert.DoesNotContain(runner.Calls, args => args.Contains("dbcontext"));
        Assert.Equal(["BlogContext", "AuditContext"], viewModel.Contexts.Select(c => c.Name));

        // Refresh is still the way to pick up a genuinely changed set of contexts.
        await viewModel.RefreshContextsCommand.ExecuteAsync(null);
        Assert.Contains(runner.Calls, args => args.Contains("dbcontext"));
    }

    [Fact]
    public async Task A_remembered_list_fills_the_dropdown_even_in_manual_mode()
    {
        var solution = CreateSolution();
        await OpenAsync(NewViewModel(new RoutingRunner()), solution);

        var runner = new RoutingRunner();
        var viewModel = NewViewModel(runner, SettingsWith(solution, DiscoveryMode.Manual));
        await OpenAsync(viewModel, solution);

        Assert.DoesNotContain(runner.Calls, args => args.Contains("dbcontext"));
        Assert.Equal(2, viewModel.Contexts.Count);
    }

    [Fact]
    public async Task Manual_mode_opens_the_workspace_without_building_anything()
    {
        var solution = CreateSolution();
        var settings = SettingsStore.Load(SettingsPath);
        settings.For(solution).Discovery = DiscoveryMode.Manual;
        var runner = new RoutingRunner();
        var viewModel = NewViewModel(runner, settings);

        await OpenAsync(viewModel, solution);

        Assert.Empty(viewModel.Contexts);
        Assert.DoesNotContain(runner.Calls, args => args.Contains("dbcontext"));
        Assert.Contains("Manual", viewModel.Session.StatusMessage);

        // Refresh is the escape hatch and must work regardless of the mode.
        await viewModel.RefreshContextsCommand.ExecuteAsync(null);

        Assert.Equal(["BlogContext", "AuditContext"], viewModel.Contexts.Select(c => c.Name));
    }

    [Fact]
    public async Task AutoNoBuildFirst_tries_without_a_build_then_retries_with_one()
    {
        var solution = CreateSolution();
        var settings = SettingsStore.Load(SettingsPath);
        settings.For(solution).Discovery = DiscoveryMode.AutoNoBuildFirst;
        var runner = new RoutingRunner { NoBuildFails = true };
        var viewModel = NewViewModel(runner, settings);

        await OpenAsync(viewModel, solution);

        var contextCalls = runner.Calls.Where(a => a.Contains("dbcontext")).ToList();
        Assert.Equal(2, contextCalls.Count);
        Assert.Contains("--no-build", contextCalls[0]);
        Assert.DoesNotContain("--no-build", contextCalls[1]);
        Assert.Equal(["BlogContext", "AuditContext"], viewModel.Contexts.Select(c => c.Name));
    }

    [Fact]
    public async Task AutoNoBuildFirst_does_not_rebuild_when_the_fast_path_works()
    {
        var solution = CreateSolution();
        var settings = SettingsStore.Load(SettingsPath);
        settings.For(solution).Discovery = DiscoveryMode.AutoNoBuildFirst;
        var runner = new RoutingRunner { NoBuildFails = false };
        var viewModel = NewViewModel(runner, settings);

        await OpenAsync(viewModel, solution);

        Assert.Single(runner.Calls, a => a.Contains("dbcontext"));
    }

    [Theory]
    [InlineData(DiscoveryMode.Auto)]
    [InlineData(DiscoveryMode.Cached)]
    [InlineData(DiscoveryMode.AutoNoBuildFirst)]
    public async Task Skip_build_is_honoured_in_every_discovery_mode(DiscoveryMode mode)
    {
        // Regression: the Skip build checkbox used to be overridden with "false" by every mode
        // except AutoNoBuildFirst, so ticking it did nothing.
        var solution = CreateSolution();
        var settings = SettingsWith(solution, mode);
        settings.For(solution).NoBuild = true;
        var runner = new RoutingRunner();
        var viewModel = NewViewModel(runner, settings);

        await OpenAsync(viewModel, solution);

        Assert.True(viewModel.NoBuild);
        Assert.All(
            runner.Calls.Where(a => a.Contains("dbcontext")),
            args => Assert.Contains("--no-build", args));
    }

    [Fact]
    public async Task Skip_build_is_honoured_by_a_manual_refresh()
    {
        var solution = CreateSolution();
        var runner = new RoutingRunner();
        var viewModel = NewViewModel(runner, SettingsWith(solution, DiscoveryMode.Manual));
        await OpenAsync(viewModel, solution);

        viewModel.NoBuild = true;
        await viewModel.RefreshContextsCommand.ExecuteAsync(null);

        Assert.Contains("--no-build", runner.Calls.Last(a => a.Contains("dbcontext")));

        viewModel.NoBuild = false;
        await viewModel.RefreshContextsCommand.ExecuteAsync(null);

        Assert.DoesNotContain("--no-build", runner.Calls.Last(a => a.Contains("dbcontext")));
    }

    [Fact]
    public async Task Skip_build_is_not_silently_undone_when_the_no_build_attempt_fails()
    {
        // The retry-with-a-build fallback exists for AutoNoBuildFirst's own guess. If the user asked
        // to skip the build, the failure is theirs to see, not something to quietly override.
        var solution = CreateSolution();
        var settings = SettingsWith(solution, DiscoveryMode.AutoNoBuildFirst);
        settings.For(solution).NoBuild = true;
        var runner = new RoutingRunner { NoBuildFails = true };
        var viewModel = NewViewModel(runner, settings);

        await OpenAsync(viewModel, solution);

        Assert.Single(runner.Calls, a => a.Contains("dbcontext"));
        Assert.Contains("no build found", viewModel.Session.StatusMessage);
    }

    [Fact]
    public async Task Selections_survive_a_restart()
    {
        var solution = CreateSolution();
        var first = NewViewModel(new RoutingRunner());
        await OpenAsync(first, solution);

        first.SelectedContext = first.Contexts.Last();
        first.DiscoveryMode = DiscoveryMode.Manual;
        first.NoBuild = true;

        // A fresh view model reading the same settings file is the restart.
        var second = NewViewModel(new RoutingRunner());
        await OpenAsync(second, solution);

        Assert.Equal(DiscoveryMode.Manual, second.DiscoveryMode);
        Assert.True(second.NoBuild);
        Assert.Contains(second.RecentWorkspaces, r => r.Path == solution);
        Assert.Equal("AuditContext", second.SelectedContext?.Name);
    }

    [Fact]
    public async Task Refresh_keeps_the_chosen_context_rather_than_jumping_to_the_first()
    {
        var solution = CreateSolution();
        var runner = new RoutingRunner();
        var viewModel = NewViewModel(runner);
        await OpenAsync(viewModel, solution);

        viewModel.SelectedContext = viewModel.Contexts.Last();
        await viewModel.RefreshContextsCommand.ExecuteAsync(null);

        Assert.Equal("AuditContext", viewModel.SelectedContext?.Name);
    }

    [Fact]
    public async Task Refresh_leaves_the_migration_tab_commands_enabled()
    {
        var solution = CreateSolution();
        var runner = new RoutingRunner();
        var viewModel = NewViewModel(runner);
        await OpenAsync(viewModel, solution);

        // Stand in for the ComboBox: Avalonia drops SelectedItem when its items are reset, and pushes
        // that null back down the two-way binding. Refresh clears and repopulates Contexts, so the
        // selection goes null and is then restored — the whole reason the commands go stale.
        viewModel.Contexts.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                viewModel.SelectedContext = null;
            }
        };

        // What the button actually shows is the last CanExecuteChanged it was told about, not a live
        // read of CanExecute — so that is what has to be asserted.
        var refreshEnabled = viewModel.Migrations.RefreshCommand.CanExecute(null);
        viewModel.Migrations.RefreshCommand.CanExecuteChanged +=
            (_, _) => refreshEnabled = viewModel.Migrations.RefreshCommand.CanExecute(null);

        var generateEnabled = viewModel.Script.GenerateCommand.CanExecute(null);
        viewModel.Script.GenerateCommand.CanExecuteChanged +=
            (_, _) => generateEnabled = viewModel.Script.GenerateCommand.CanExecute(null);

        await viewModel.RefreshContextsCommand.ExecuteAsync(null);

        // The commands read their target through a delegate that raises nothing of its own, so they
        // only notice the selection coming back if they are told to re-check. Without that, the last
        // thing the buttons heard was the transient null, and they stay greyed out until some
        // unrelated change — clicking the sort toggle — happens to notify them again.
        Assert.NotNull(viewModel.SelectedContext);
        Assert.True(refreshEnabled);
        Assert.True(generateEnabled);
    }

    [Fact]
    public async Task Restoring_a_workspace_leaves_the_migration_tab_commands_enabled()
    {
        var solution = CreateSolution();
        await OpenAsync(NewViewModel(new RoutingRunner()), solution);

        // Second open restores the remembered selections with _restoring set throughout.
        var reopened = NewViewModel(new RoutingRunner());
        await OpenAsync(reopened, solution);

        Assert.NotNull(reopened.SelectedContext);
        Assert.True(reopened.Migrations.RefreshCommand.CanExecute(null));
        Assert.True(reopened.Script.GenerateCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_context_that_no_longer_exists_falls_back_to_the_first()
    {
        var solution = CreateSolution();
        var runner = new RoutingRunner();
        var viewModel = NewViewModel(runner);
        await OpenAsync(viewModel, solution);
        viewModel.SelectedContext = viewModel.Contexts.Last();

        runner.ContextNames = ["BlogContext"];
        await viewModel.RefreshContextsCommand.ExecuteAsync(null);

        Assert.Equal("BlogContext", viewModel.SelectedContext?.Name);
    }

    [Fact]
    public async Task Opening_the_folder_and_the_solution_inside_it_are_the_same_workspace()
    {
        var solution = CreateSolution();
        var first = NewViewModel(new RoutingRunner());
        await OpenAsync(first, solution);
        first.DiscoveryMode = DiscoveryMode.Manual;

        var second = NewViewModel(new RoutingRunner());
        await OpenAsync(second, _root);

        Assert.Equal(DiscoveryMode.Manual, second.DiscoveryMode);
        Assert.Single(second.RecentWorkspaces);
    }

    [Fact]
    public async Task A_missing_ef_tool_blocks_discovery_and_surfaces_the_install_command()
    {
        var solution = CreateSolution();
        var runner = new RoutingRunner { EfToolProblem = "Could not execute because the command was not found." };
        var viewModel = NewViewModel(runner);

        await OpenAsync(viewModel, solution);

        Assert.True(viewModel.HasPreflightProblem);
        Assert.DoesNotContain(runner.Calls, args => args.Contains("dbcontext"));
        Assert.Equal("dotnet tool install --global dotnet-ef", MainWindowViewModel.InstallCommand);
    }

    [Fact]
    public async Task Updating_the_ef_tool_reports_success_in_the_status_bar_and_the_icon()
    {
        var viewModel = NewViewModel(new RoutingRunner());

        await viewModel.UpdateEfToolCommand.ExecuteAsync(null);

        Assert.True(viewModel.EfToolUpdateSucceeded);
        Assert.False(viewModel.HasEfToolUpdateError);
        Assert.Equal("dotnet-ef update finished.", viewModel.Session.StatusMessage);
    }

    [Fact]
    public async Task A_failed_ef_tool_update_reports_the_failure_instead_of_hanging_on_the_verb()
    {
        var runner = new RoutingRunner { ToolUpdateError = "Tool 'dotnet-ef' failed to update." };
        var viewModel = NewViewModel(runner);

        await viewModel.UpdateEfToolCommand.ExecuteAsync(null);

        Assert.False(viewModel.EfToolUpdateSucceeded);
        Assert.True(viewModel.HasEfToolUpdateError);
        Assert.Equal("Tool 'dotnet-ef' failed to update.", viewModel.EfToolUpdateErrorSummary);
        Assert.Contains("Tool 'dotnet-ef' failed to update.", viewModel.EfToolUpdateErrorDetail);
        Assert.Equal("Tool 'dotnet-ef' failed to update.", viewModel.Session.StatusMessage);
    }

    [Fact]
    public async Task The_versions_line_carries_the_migrations_projects_resolved_ef_core_version()
    {
        var solution = CreateSolution();
        WriteAssets(@"src\Data", "10.0.10");
        var viewModel = NewViewModel(new RoutingRunner());

        await OpenAsync(viewModel, solution);

        Assert.Equal("dotnet-ef 10.0.10 · EF Core 10.0.10 · SDK 10.0.400", viewModel.EnvironmentSummary);
    }

    [Fact]
    public async Task A_project_on_a_newer_ef_core_than_the_tool_is_called_out_in_the_versions_line()
    {
        var solution = CreateSolution();
        WriteAssets(@"src\Data", "11.0.0");
        var viewModel = NewViewModel(new RoutingRunner());

        await OpenAsync(viewModel, solution);

        Assert.Contains("EF Core 11.0.0 (newer than the tool)", viewModel.EnvironmentSummary);
    }

    [Fact]
    public async Task The_startup_project_supplies_the_version_when_the_migrations_project_has_none()
    {
        var solution = CreateSolution();
        WriteAssets(@"src\Api", "10.0.10");
        var viewModel = NewViewModel(new RoutingRunner());

        await OpenAsync(viewModel, solution);

        Assert.Contains("EF Core 10.0.10", viewModel.EnvironmentSummary);
    }

    [Fact]
    public async Task An_unrestored_workspace_shows_the_versions_it_has_rather_than_a_placeholder()
    {
        var solution = CreateSolution();
        var viewModel = NewViewModel(new RoutingRunner());

        await OpenAsync(viewModel, solution);

        Assert.Equal("dotnet-ef 10.0.10 · SDK 10.0.400", viewModel.EnvironmentSummary);
    }

    [Fact]
    public async Task Switching_the_migrations_project_moves_the_version_with_it()
    {
        var solution = CreateSolution();
        WriteAssets(@"src\Api", "9.0.11");
        WriteAssets(@"src\Data", "10.0.10");
        var viewModel = NewViewModel(new RoutingRunner());

        await OpenAsync(viewModel, solution);
        Assert.Contains("EF Core 10.0.10", viewModel.EnvironmentSummary);

        viewModel.MigrationsProject = viewModel.Projects.Single(p => p.Name == "Api");

        Assert.Contains("EF Core 9.0.11", viewModel.EnvironmentSummary);
    }

    [Fact]
    public async Task The_command_is_echoed_to_the_output_console_verbatim()
    {
        var solution = CreateSolution();
        var viewModel = NewViewModel(new RoutingRunner());

        await OpenAsync(viewModel, solution);

        // "Every command executed is visible verbatim" — the user must be able to reproduce it.
        Assert.Contains(
            viewModel.Session.Output,
            line => line.Text.StartsWith("> dotnet ef dbcontext list", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Selecting_the_script_tab_probes_the_provider()
    {
        // The probe builds, so it deliberately does not run on every context change. Index 1 is the
        // Script tab.
        var solution = CreateSolution();
        var runner = new RoutingRunner();
        var viewModel = NewViewModel(runner);
        await OpenAsync(viewModel, solution);

        var before = runner.Calls.Count(a => a.Contains("dbcontext") && a.Contains("info"));
        viewModel.SelectedTabIndex = 1;
        await Task.Yield();

        Assert.True(
            runner.Calls.Count(a => a.Contains("dbcontext") && a.Contains("info")) > before,
            "selecting the Script tab should read the provider details");
    }

    [Fact]
    public async Task Closing_a_workspace_returns_to_the_landing_state()
    {
        var solution = CreateSolution();
        var viewModel = NewViewModel(new RoutingRunner());
        await OpenAsync(viewModel, solution);

        viewModel.CloseWorkspaceCommand.Execute(null);

        Assert.False(viewModel.HasWorkspace);
        Assert.Null(viewModel.WorkspacePath);
        Assert.Empty(viewModel.Contexts);
        Assert.Empty(viewModel.Projects);
    }

    /// <summary>
    /// The Phase 2 exit criteria, end to end: a real project, the real runner, the real CLI.
    /// Slower than the rest because it builds the sample, but it is the only test that would catch a
    /// wrong flag reaching dotnet ef.
    /// </summary>
    [Fact]
    public async Task Opening_the_sample_project_populates_the_context_dropdown_for_real()
    {
        var sample = Path.Combine(RepositoryRoot(), "samples", "SampleEfApp");
        Assert.True(Directory.Exists(sample), $"sample project not found at {sample}");

        // The real runner reports from the process's reader threads, so this one needs the
        // collection guarded. xunit gives the test an ordered synchronization context, which is
        // enough to serialize the appends.
        var context = SynchronizationContext.Current;
        var viewModel = new MainWindowViewModel(new EfRunner(), new AppSettings(), SettingsPath)
        {
            PostToUiThread = context is null ? action => action() : action => context.Post(_ => action(), null),
            PickFolderAsync = () => Task.FromResult<string?>(sample),
        };

        await viewModel.OpenFolderCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasPreflightProblem, viewModel.PreflightProblem);
        Assert.Equal("SampleEfApp", viewModel.MigrationsProject?.Name);
        Assert.Equal(
            ["BlogContext", "AuditContext"],
            viewModel.Contexts.Select(c => c.Name).Order(StringComparer.Ordinal).Reverse());

        // The one place the assets-file shape is checked against a real restore rather than
        // hand-written JSON. The command above built the sample, so there is one to read.
        Assert.Contains("EF Core ", viewModel.EnvironmentSummary);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EfAssist.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public void Word_wrap_is_app_wide_and_saves_with_no_workspace_open()
    {
        var viewModel = NewViewModel(new RoutingRunner());
        Assert.False(viewModel.WrapOutput);

        viewModel.WrapOutput = true;

        // Persist() bails out when no workspace is open, so this needs its own save path.
        Assert.True(SettingsStore.Load(SettingsPath).Display.WrapOutput);
        Assert.True(NewViewModel(new RoutingRunner()).WrapOutput);
    }

    [Fact]
    public void Theme_is_app_wide_and_saves_with_no_workspace_open()
    {
        var viewModel = NewViewModel(new RoutingRunner());
        Assert.Equal(AppTheme.System, viewModel.Appearance.Theme);

        viewModel.Appearance.Theme = AppTheme.Dark;

        // Same reasoning as word wrap: Persist() bails out with no workspace open, so the theme
        // needs its own save path or the choice is lost on the landing screen.
        Assert.Equal(AppTheme.Dark, SettingsStore.Load(SettingsPath).Display.Theme);
        Assert.Equal(AppTheme.Dark, NewViewModel(new RoutingRunner()).Appearance.Theme);
    }

    [Fact]
    public async Task Copy_all_output_copies_every_line()
    {
        var solution = CreateSolution();
        var viewModel = NewViewModel(new RoutingRunner());
        string? copied = null;
        viewModel.CopyToClipboardAsync = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };

        await OpenAsync(viewModel, solution);
        await viewModel.Session.CopyOutputCommand.ExecuteAsync(null);

        Assert.NotNull(copied);
        Assert.Equal(
            string.Join(Environment.NewLine, viewModel.Session.Output.Select(l => l.Text)),
            copied);
    }

    [Fact]
    public void Recent_entries_split_into_a_name_and_a_location()
    {
        var solution = CreateSolution();

        var fromSolution = RecentWorkspace.FromPath(solution);
        Assert.Equal("Thing", fromSolution.Name);
        Assert.Equal(_root.TrimEnd(Path.DirectorySeparatorChar), fromSolution.Location.TrimEnd(Path.DirectorySeparatorChar));
        Assert.Equal(solution, fromSolution.Path);

        // A folder workspace has no file name to use, so the folder itself is the name.
        var fromFolder = RecentWorkspace.FromPath(_root);
        Assert.Equal(Path.GetFileName(_root), fromFolder.Name);
        Assert.Equal(_root, fromFolder.Path);
    }

    [Fact]
    public async Task Diagnostics_carry_the_environment_and_the_last_command()
    {
        var solution = CreateSolution();
        var viewModel = NewViewModel(new RoutingRunner());
        string? copied = null;
        viewModel.CopyToClipboardAsync = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };

        await OpenAsync(viewModel, solution);
        await viewModel.Session.CopyDiagnosticsCommand.ExecuteAsync(null);

        Assert.NotNull(copied);
        Assert.Contains("dotnet-ef 10.0.10", copied);
        Assert.Contains("Workspace:", copied);
        Assert.Contains("Exit code:", copied);
    }

    [Fact]
    public async Task A_failed_context_refresh_keeps_the_list_and_marks_it_out_of_date()
    {
        var solution = CreateSolution();
        var runner = new RoutingRunner();
        var viewModel = NewViewModel(runner);

        await OpenAsync(viewModel, solution);
        Assert.Equal(2, viewModel.Contexts.Count);
        Assert.False(viewModel.ContextsStale);

        runner.ContextsFail = true;
        await viewModel.RefreshContextsCommand.ExecuteAsync(null);

        // Losing the selection over a failed refresh would be worse than showing an old one.
        Assert.Equal(2, viewModel.Contexts.Count);
        Assert.True(viewModel.ShowsStaleContexts);

        // And the failure is explained rather than left as EF's own wording alone.
        Assert.True(viewModel.Session.HasDiagnosis);
        Assert.Contains("did not compile", viewModel.Session.Diagnosis!.Title);

        runner.ContextsFail = false;
        await viewModel.RefreshContextsCommand.ExecuteAsync(null);

        Assert.False(viewModel.ContextsStale);
        Assert.False(viewModel.Session.HasDiagnosis);
    }

    [Fact]
    public void Run_option_count_tracks_the_three_switches_wherever_they_live()
    {
        var viewModel = NewViewModel(new RoutingRunner());

        Assert.Equal(0, viewModel.ActiveRunOptionCount);
        Assert.False(viewModel.HasActiveRunOptions);

        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        viewModel.NoBuild = true;
        viewModel.Idempotent = true;

        // Offline lives on the migrations list, not on the shell, and still has to be counted.
        viewModel.Migrations.Offline = true;

        Assert.Equal(3, viewModel.ActiveRunOptionCount);
        Assert.True(viewModel.HasActiveRunOptions);

        // The badge is bound, so every switch has to raise the change - including the one that
        // arrives from another view model.
        Assert.Equal(3, changes.Count(name => name == nameof(MainWindowViewModel.ActiveRunOptionCount)));

        viewModel.NoBuild = false;
        viewModel.Idempotent = false;
        viewModel.Migrations.Offline = false;

        Assert.Equal(0, viewModel.ActiveRunOptionCount);
        Assert.False(viewModel.HasActiveRunOptions);
    }
}
