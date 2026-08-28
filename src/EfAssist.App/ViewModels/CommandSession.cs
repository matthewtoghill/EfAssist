using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EfAssist.Core;

namespace EfAssist.App.ViewModels;

/// <summary>
/// Runs one <c>dotnet ef</c> command at a time and owns the output console. Shared by every tab, so
/// they all queue behind each other and all stream into the same console — these commands mutate a
/// database and a filesystem, and running two at once is a way to corrupt both.
/// </summary>
public partial class CommandSession : ObservableObject
{
    private readonly IEfRunner _runner;
    private CancellationTokenSource? _cancellation;

    public CommandSession(IEfRunner runner) => _runner = runner;

    /// <summary>Supplied by the view, which owns the TopLevel the clipboard needs.</summary>
    public Func<string, Task>? CopyToClipboardAsync { get; set; }

    /// <summary>
    /// How to get back onto the UI thread. Output lines arrive on the process's stdout/stderr reader
    /// threads, and <see cref="ObservableCollection{T}"/> must not be touched from those. Relying on
    /// an ambient <see cref="SynchronizationContext"/> here is what made this racy once already, so
    /// the marshalling is explicit — tests replace it with a direct invoke.
    /// </summary>
    public Action<Action> PostToUiThread { get; set; } = action => Dispatcher.UIThread.Post(action);

    /// <summary>Where commands launch from. Set by the shell when a workspace opens.</summary>
    public string WorkingDirectory { get; set; } = AppContext.BaseDirectory;

    /// <summary>Context the shell prepends to the diagnostics block.</summary>
    public Func<string>? DiagnosticsHeader { get; set; }

    /// <summary>The last command's full result, for diagnostics.</summary>
    public EfResult? LastResult { get; private set; }

    public ObservableCollection<OutputLine> Output { get; } = [];

    /// <summary>
    /// What has run this session, newest first, capped. Every entry keeps its own outcome and
    /// diagnosis, so a failure stays attached to the command that caused it instead of scrolling
    /// away up the console. In memory only — see <see cref="CommandRun"/>.
    /// </summary>
    public ObservableCollection<CommandRun> Runs { get; } = [];

    /// <summary>How many runs are kept. Beyond this the oldest is dropped.</summary>
    public const int MaxRuns = 50;

    /// <summary>The most recent run, which is what the folded output strip reports.</summary>
    public CommandRun? LastRun => Runs.Count > 0 ? Runs[0] : null;

    /// <summary>
    /// A failure has been recorded that the user has not looked at yet. Cleared by
    /// <see cref="MarkActivityRead"/> when the Activity list is opened.
    /// </summary>
    [ObservableProperty]
    private bool _hasUnreadFailure;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusMessage = "Ready.";

    /// <summary>
    /// Plain-language guidance for the last failure, when it was one we recognise. Null the rest of
    /// the time, including after a success, so it can never describe a problem that has been fixed.
    /// </summary>
    [ObservableProperty]
    private EfDiagnosis? _diagnosis;

    public bool HasDiagnosis => Diagnosis is not null;

    /// <summary>
    /// Runs a command, streaming its output. Returns null when another command is already running or
    /// the user cancelled — in both cases the caller should do nothing further.
    /// </summary>
    public async Task<EfResult?> RunAsync(
        IReadOnlyList<string> args,
        string label,
        bool destructive = false)
    {
        if (IsRunning)
        {
            return null;
        }

        IsRunning = true;
        Diagnosis = null;
        _cancellation = new CancellationTokenSource();
        StatusMessage = $"{label}…";

        // Where this run's output starts, for the Activity card's "Show in raw output".
        var firstLine = Output.Count;
        var started = DateTimeOffset.Now;
        var clock = Stopwatch.StartNew();

        // Echo the command verbatim so it can be reproduced in a terminal.
        Append(new OutputLine(OutputChannel.Info, "> dotnet " + string.Join(' ', args)));

        try
        {
            var result = await _runner.RunAsync(
                args,
                WorkingDirectory,
                new LineProgress(Append),
                _cancellation.Token);

            LastResult = result;
            Diagnosis = EfDiagnostics.Diagnose(result);

            Record(new CommandRun
            {
                Label = label,
                Args = args,
                Outcome = result.Success ? CommandOutcome.Succeeded : CommandOutcome.Failed,
                ExitCode = result.ExitCode,
                Duration = clock.Elapsed,
                StartedAt = started,
                Diagnosis = Diagnosis,
                FailureLine = result.Success ? null : FirstLine(result.ErrorMessage),
                FirstOutputLine = firstLine,
                Destructive = destructive,
            });

            return result;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"{label} cancelled.";
            Append(new OutputLine(OutputChannel.Warn, $"{label} cancelled."));
            Record(new CommandRun
            {
                Label = label,
                Args = args,
                Outcome = CommandOutcome.Cancelled,
                Duration = clock.Elapsed,
                StartedAt = started,
                FirstOutputLine = firstLine,
                Destructive = destructive,
            });
            return null;
        }
        catch (Exception ex)
        {
            // Anything the runner did not turn into a failed result — a broken pipe, a disposed
            // process handle. Without this the command dies silently and the status bar keeps
            // claiming the command is still going.
            StatusMessage = $"{label} failed: {ex.Message}";
            Append(new OutputLine(OutputChannel.Error, ex.ToString()));
            Record(new CommandRun
            {
                Label = label,
                Args = args,
                Outcome = CommandOutcome.Failed,
                Duration = clock.Elapsed,
                StartedAt = started,
                FailureLine = ex.Message,
                FirstOutputLine = firstLine,
                Destructive = destructive,
            });
            return null;
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            IsRunning = false;
        }
    }

    /// <summary>
    /// Runs work that happens in this process rather than in <c>dotnet ef</c>, under the same busy
    /// state, the same Cancel button and the same status line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Diagram generation reads and parses a file; there is no process to launch. Giving it its own
    /// cancellation source and its own Cancel button would mean two notions of "busy" on one screen,
    /// and would let a diagram be generated while a migration was being applied — which is exactly
    /// what the one-command-at-a-time rule exists to prevent, since generating reads files a running
    /// <c>migrations add</c> is in the middle of writing.
    /// </para>
    /// <para>
    /// Deliberately does not touch <see cref="LastResult"/> or <see cref="Diagnosis"/>. There is no
    /// <see cref="EfResult"/> behind this, and inventing one would put a fabricated command line into
    /// the "Copy diagnostics" block.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The work's result, or <c>default</c> when another command is already running, the user
    /// cancelled, or the work threw — in every one of those cases the caller should do nothing
    /// further.
    /// </returns>
    public async Task<T?> RunLocalAsync<T>(string label, Func<CancellationToken, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (IsRunning)
        {
            return default;
        }

        IsRunning = true;
        Diagnosis = null;
        _cancellation = new CancellationTokenSource();
        StatusMessage = $"{label}…";

        var firstLine = Output.Count;
        var started = DateTimeOffset.Now;
        var clock = Stopwatch.StartNew();

        try
        {
            var value = await work(_cancellation.Token);

            // No Args: there is no command line to repeat, so the card shows the label alone and
            // offers no "Run again".
            Record(new CommandRun
            {
                Label = label,
                Outcome = CommandOutcome.Succeeded,
                Duration = clock.Elapsed,
                StartedAt = started,
                FirstOutputLine = firstLine,
            });

            return value;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"{label} cancelled.";
            Append(new OutputLine(OutputChannel.Warn, $"{label} cancelled."));
            Record(new CommandRun
            {
                Label = label,
                Outcome = CommandOutcome.Cancelled,
                Duration = clock.Elapsed,
                StartedAt = started,
                FirstOutputLine = firstLine,
            });
            return default;
        }
        catch (Exception ex)
        {
            StatusMessage = $"{label} failed: {ex.Message}";
            Append(new OutputLine(OutputChannel.Error, ex.ToString()));
            Record(new CommandRun
            {
                Label = label,
                Outcome = CommandOutcome.Failed,
                Duration = clock.Elapsed,
                StartedAt = started,
                FailureLine = ex.Message,
                FirstOutputLine = firstLine,
            });
            return default;
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            IsRunning = false;
        }
    }

    /// <summary>
    /// Adds a run to the head of the list, dropping the oldest beyond <see cref="MaxRuns"/>. Marshalled
    /// like output is: a command can finish on a runner thread.
    /// </summary>
    private void Record(CommandRun run) => PostToUiThread(() =>
    {
        Runs.Insert(0, run);

        while (Runs.Count > MaxRuns)
        {
            Runs.RemoveAt(Runs.Count - 1);
        }

        if (run.Failed)
        {
            HasUnreadFailure = true;
        }

        OnPropertyChanged(nameof(LastRun));
    });

    /// <summary>Called when the Activity list is shown: the failure has been seen.</summary>
    public void MarkActivityRead() => HasUnreadFailure = false;

    /// <summary>Reports a failure using EF's own message, which is already readable.</summary>
    public void ReportFailure(EfResult result, string fallback) =>
        StatusMessage = result.ErrorMessage.Length > 0 ? FirstLine(result.ErrorMessage) : fallback;

    public void Reset()
    {
        Output.Clear();
        // The recorded runs point into the console by line index, so they cannot outlive it.
        Runs.Clear();
        OnPropertyChanged(nameof(LastRun));
        HasUnreadFailure = false;
        LastResult = null;
        Diagnosis = null;
        StatusMessage = "Ready.";
    }

    /// <summary>Hides the guidance panel without touching the output it describes.</summary>
    [RelayCommand]
    private void DismissDiagnosis() => Diagnosis = null;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel()
    {
        // Kills the whole process tree — MSBuild leaves node processes behind otherwise.
        if (_cancellation is not null)
        {
            _cancellation.Cancel();
            StatusMessage = "Cancelling…";
        }
    }

    [RelayCommand]
    private void ClearOutput() => Output.Clear();

    /// <summary>
    /// The console allows per-line selection, so this covers the multi-line case without making the
    /// user reach for the full diagnostics block.
    /// </summary>
    [RelayCommand]
    private async Task CopyOutputAsync()
    {
        if (CopyToClipboardAsync is null)
        {
            return;
        }

        await CopyToClipboardAsync(string.Join(Environment.NewLine, Output.Select(l => l.Text)));
        StatusMessage = $"Copied {Output.Count} output lines to the clipboard.";
    }

    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        if (CopyToClipboardAsync is null)
        {
            return;
        }

        var header = DiagnosticsHeader?.Invoke() ?? "";
        var diagnosis = Diagnosis is null
            ? ""
            : $"Diagnosis: {Diagnosis.Title}{Environment.NewLine}           {Diagnosis.Guidance}{Environment.NewLine}{Environment.NewLine}";
        var body = LastResult?.Diagnostics ?? "No command has been run yet.";

        await CopyToClipboardAsync(header + Environment.NewLine + diagnosis + body);
        StatusMessage = "Diagnostics copied to the clipboard.";
    }

    private void Append(OutputLine line) => PostToUiThread(() => Output.Add(line));

    private static string FirstLine(string text)
    {
        var newline = text.IndexOf('\n');
        return newline < 0 ? text : text[..newline].TrimEnd('\r');
    }

    partial void OnIsRunningChanged(bool value) => CancelCommand.NotifyCanExecuteChanged();

    partial void OnDiagnosisChanged(EfDiagnosis? value) => OnPropertyChanged(nameof(HasDiagnosis));

    /// <summary>Adapts <see cref="Append"/> to <see cref="IProgress{T}"/> without a captured context.</summary>
    private sealed class LineProgress(Action<OutputLine> append) : IProgress<OutputLine>
    {
        public void Report(OutputLine value) => append(value);
    }
}
