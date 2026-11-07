// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;

namespace ArkPar.Tests.Screens;

/// <summary>Stands in for the engine's trial: records each estimate a screen asks for and lets the test answer it.</summary>
internal sealed class FakeTrial
{
    private readonly List<PendingTrial> _trials = [];

    public IReadOnlyList<PendingTrial> Trials => _trials;

    public PendingTrial Last => _trials[^1];

    public Task<RecoveryEstimate> RunAsync(EngineCommand.Create create, CancellationToken stop)
    {
        var trial = new PendingTrial(create, stop);
        _trials.Add(trial);
        return trial.Answer;
    }
}

internal sealed class PendingTrial
{
    private readonly TaskCompletionSource<RecoveryEstimate> _answer = new();

    public PendingTrial(EngineCommand.Create create, CancellationToken stop)
    {
        Create = create;
        Stop = stop;
        stop.Register(() => _answer.TrySetCanceled(stop));
    }

    public EngineCommand.Create Create { get; }

    /// <summary>Signaled when the screen no longer wants this answer.</summary>
    public CancellationToken Stop { get; }

    public Task<RecoveryEstimate> Answer => _answer.Task;

    public void Finish(RecoveryEstimate estimate) => _answer.SetResult(estimate);

    public void Fail(Exception failure) => _answer.SetException(failure);
}
