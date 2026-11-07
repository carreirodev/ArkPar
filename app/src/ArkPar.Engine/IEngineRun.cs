// SPDX-License-Identifier: GPL-2.0-or-later

namespace ArkPar.Engine;

/// <summary>One engine task in progress, as the screens drive it (<see cref="EngineRun"/>, or a fake in tests).</summary>
public interface IEngineRun : IDisposable
{
    /// <summary>Ends when the engine has ended, with how it ended.</summary>
    Task<EngineResult> Completion { get; }

    bool IsPaused { get; }

    /// <summary>Pauses or resumes the task. Does nothing if it is already in that state, canceled or finished.</summary>
    void SetPaused(bool paused);

    /// <summary>Stops the task; it then ends as <see cref="EngineOutcome.Canceled"/>, unless it finished first.</summary>
    void Cancel();
}
