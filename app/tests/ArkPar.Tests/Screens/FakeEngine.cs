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

    /// <summary>
    /// Writes what a create leaves in its set's folder, under the name it was given (a draft's, while a set is
    /// made): the index and one recovery file.
    /// </summary>
    public void WriteSet(int indexSize = 100, int recoveryFileSize = 100, string recoveryFileEnd = ".vol0+10.par2")
    {
        var index = Command.RecoveryFile;
        File.WriteAllBytes(index, new byte[indexSize]);
        File.WriteAllBytes(Path.ChangeExtension(index, null) + recoveryFileEnd, new byte[recoveryFileSize]);
    }

    public void Finish(EngineResult result) => _completion.SetResult(result);

    public void Fail(Exception failure) => _completion.SetException(failure);

    public void SetPaused(bool paused) => IsPaused = paused;

    public void Cancel() => CancelRequested = true;

    public void Dispose() => Disposed = true;
}
