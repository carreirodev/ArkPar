// SPDX-License-Identifier: GPL-2.0-or-later

namespace ArkPar.Engine;

/// <summary>How an engine task ended.</summary>
/// <param name="Outcome">The result to show.</param>
/// <param name="ExitCode">The engine's raw exit code (bits in source/par2j/Command_par2j.txt), for the log.</param>
/// <param name="Report">
/// For verify and repair, the state of each photo as found before any repair; otherwise null.
/// Also null when the task was canceled or failed.
/// </param>
/// <param name="Output">Everything the engine printed, for the log.</param>
public sealed record EngineResult(EngineOutcome Outcome, int ExitCode, VerificationReport? Report, string Output)
{
    /// <summary>A recovery file of the set is damaged or missing, even if every photo is fine.</summary>
    public bool RecoveryFilesIncomplete => Outcome != EngineOutcome.Canceled && EngineOutcomes.RecoveryFilesIncomplete(ExitCode);
}
