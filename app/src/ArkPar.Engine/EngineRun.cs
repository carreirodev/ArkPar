// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine.Native;

namespace ArkPar.Engine;

/// <summary>
/// One engine task in progress: reports its progress, can be paused, resumed and canceled,
/// and ends with an <see cref="EngineResult"/>.
/// </summary>
/// <remarks>
/// The engine runs in a hidden console and is steered with the keys it reads there: <c>p</c>, <c>r</c>
/// and <c>c</c> (source/par2j/common2.c, print_progress). It checks them at each progress update,
/// about once a second, so a pause or cancel takes effect within about a second. A pause stops the
/// task, but the calculation block already under way finishes first.
/// </remarks>
public sealed class EngineRun : IEngineRun
{
    /// <summary>How long a canceled engine gets to stop by itself before it is ended at once.</summary>
    internal static readonly TimeSpan DefaultCancelGracePeriod = TimeSpan.FromSeconds(5);

    private readonly HiddenConsoleProcess _process;
    private readonly EngineOutputReader _output;
    private readonly TaskCompletionSource _cancelRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _lock = new();
    private bool _paused;
    private bool _finished;
    private bool _killed;

    private EngineRun(EngineCommand command, string runDirectory, HiddenConsoleProcess process, IProgress<EngineProgress>? progress, TimeSpan cancelGracePeriod)
    {
        Command = command;
        _process = process;
        _output = new EngineOutputReader(progress);
        Completion = RunAsync(runDirectory, cancelGracePeriod);
    }

    /// <summary>The engine shipped with the app (copied next to it at build time).</summary>
    public static string DefaultEnginePath { get; } = Path.Combine(AppContext.BaseDirectory, "engine", "par2j64.exe");

    public EngineCommand Command { get; }

    /// <summary>Ends when the engine has ended, with how it ended.</summary>
    public Task<EngineResult> Completion { get; }

    public bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                return _paused;
            }
        }
    }

    /// <summary>Starts <paramref name="command"/>. Progress is reported on the thread that reads the engine's output.</summary>
    public static EngineRun Start(EngineCommand command, IProgress<EngineProgress>? progress = null, string? enginePath = null) =>
        Start(command, runDirectory => EngineArguments.For(command, runDirectory), progress, enginePath ?? DefaultEnginePath, DefaultCancelGracePeriod);

    /// <summary>Starts <paramref name="enginePath"/> with arguments built for a fresh run folder (tests swap them).</summary>
    internal static EngineRun Start(
        EngineCommand command,
        Func<string, IReadOnlyList<string>> arguments,
        IProgress<EngineProgress>? progress,
        string enginePath,
        TimeSpan cancelGracePeriod)
    {
        if (!File.Exists(enginePath))
        {
            throw new FileNotFoundException("The engine (par2j64.exe) was not found.", enginePath);
        }

        // A fresh folder per task for the photo list and the JSON result, so a result is never one from an earlier task.
        var runDirectory = Path.Combine(Path.GetTempPath(), "ArkPar", "runs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        try
        {
            if (command is EngineCommand.Create create)
            {
                File.WriteAllBytes(Path.Combine(runDirectory, EngineArguments.SourceListName), EngineArguments.SourceList(create));
            }

            var process = HiddenConsoleProcess.Start(enginePath, arguments(runDirectory), runDirectory);
            return new EngineRun(command, runDirectory, process, progress, cancelGracePeriod);
        }
        catch
        {
            Directory.Delete(runDirectory, recursive: true);
            throw;
        }
    }

    /// <summary>Pauses the task. Does nothing if it is already paused, canceled or finished.</summary>
    public void Pause() => SetPaused(true);

    /// <summary>Resumes a paused task.</summary>
    public void Resume() => SetPaused(false);

    /// <summary>
    /// Asks the engine to stop. If it has not stopped after a few seconds (it was in a step that does not
    /// check keys), it is ended at once. Either way the result is <see cref="EngineOutcome.Canceled"/>,
    /// unless the task finished first.
    /// </summary>
    public void Cancel()
    {
        lock (_lock)
        {
            if (_finished || _cancelRequested.Task.IsCompleted)
            {
                return;
            }

            // The engine takes 'c' even while paused.
            _process.SendKey('c');
            _cancelRequested.SetResult();
        }
    }

    /// <summary>Ends the engine at once if it is still running, e.g. when the app closes.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (!_finished)
            {
                _killed = true;
                _process.Kill();
            }
        }
    }

    /// <summary>
    /// Pauses or resumes, sending the key unless the task is already in that state, canceled or finished.
    /// The engine prints " Pause" without flushing it, so the state is tracked here, not read back.
    /// </summary>
    public void SetPaused(bool paused)
    {
        lock (_lock)
        {
            if (_finished || _cancelRequested.Task.IsCompleted || _paused == paused)
            {
                return;
            }

            _process.SendKey(paused ? 'p' : 'r');
            _paused = paused;
        }
    }

    private async Task<EngineResult> RunAsync(string runDirectory, TimeSpan cancelGracePeriod)
    {
        EngineResult result;
        try
        {
            result = await RunEngineAsync(runDirectory, cancelGracePeriod).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            // Clean up, without letting a cleanup failure hide why the run failed.
            try
            {
                CleanUp(runDirectory);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(failure, cleanupFailure);
            }

            throw;
        }

        CleanUp(runDirectory);
        return result;
    }

    private void CleanUp(string runDirectory)
    {
        lock (_lock)
        {
            _finished = true;
            _process.Dispose();
        }

        Directory.Delete(runDirectory, recursive: true);
    }

    private async Task<EngineResult> RunEngineAsync(string runDirectory, TimeSpan cancelGracePeriod)
    {
        var reading = Task.Factory.StartNew(ReadOutput, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var exit = _process.WaitForExitAsync();
        if (await Task.WhenAny(exit, _cancelRequested.Task).ConfigureAwait(false) != exit
            && await Task.WhenAny(exit, Task.Delay(cancelGracePeriod)).ConfigureAwait(false) != exit)
        {
            lock (_lock)
            {
                _killed = true;
                _process.Kill();
            }
        }

        var exitCode = await exit.ConfigureAwait(false);
        await reading.ConfigureAwait(false);

        EngineOutcome outcome;
        lock (_lock)
        {
            _finished = true;
            outcome = _killed ? EngineOutcome.Canceled : EngineOutcomes.FromExitCode(exitCode);
        }

        var report = Command is EngineCommand.Verify or EngineCommand.Repair && outcome is not (EngineOutcome.Error or EngineOutcome.Canceled)
            ? VerificationReport.Load(EngineArguments.ReportPath(Command.RecoveryFile, runDirectory), _output.Text)
            : null;
        return new EngineResult(outcome, exitCode, report, _output.Text);
    }

    private void ReadOutput()
    {
        // Raw reads, not ReadLine: a progress update ends with a bare CR, and ReadLine would hold it
        // back until the next character arrived to tell whether a LF follows.
        var buffer = new byte[4096];
        int count;
        while ((count = _process.Output.Read(buffer)) > 0)
        {
            _output.Write(buffer.AsSpan(0, count));
        }
    }
}
