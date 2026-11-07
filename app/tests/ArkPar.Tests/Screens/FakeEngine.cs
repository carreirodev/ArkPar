// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;
using ArkPar.Tests.Engine;

namespace ArkPar.Tests.Screens;

/// <summary>Stands in for the engine: records each task a screen starts and lets the test end it.</summary>
internal sealed class FakeEngine
{
    private readonly List<FakeRun> _runs = [];

    public IReadOnlyList<FakeRun> Runs => _runs;

    public FakeRun Last => _runs[^1];

    public IEngineRun Start(EngineCommand command, IProgress<EngineProgress> progress)
    {
        var run = new FakeRun(command, progress);
        _runs.Add(run);
        return run;
    }

    /// <summary>A result built from a captured real run (Engine/Fixtures).</summary>
    public static EngineResult Result(EngineOutcome outcome, int exitCode, string fixture)
    {
        var output = File.ReadAllText(Fixture.PathOf(fixture + ".txt"));
        return new EngineResult(outcome, exitCode, VerificationReport.Load(Fixture.PathOf(fixture + ".json"), output), output);
    }
}

internal sealed class FakeRun(EngineCommand command, IProgress<EngineProgress> progress) : IEngineRun
{
    private readonly TaskCompletionSource<EngineResult> _completion = new();

    public EngineCommand Command { get; } = command;

    public Task<EngineResult> Completion => _completion.Task;

    public bool IsPaused { get; private set; }

    public bool CancelRequested { get; private set; }

    public bool Disposed { get; private set; }

    public void Report(string activity, double percent) => progress.Report(new EngineProgress(activity, percent));

    public void Finish(EngineResult result) => _completion.SetResult(result);

    public void Fail(Exception failure) => _completion.SetException(failure);

    public void SetPaused(bool paused) => IsPaused = paused;

    public void Cancel() => CancelRequested = true;

    public void Dispose() => Disposed = true;
}
