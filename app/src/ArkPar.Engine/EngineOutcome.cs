// SPDX-License-Identifier: GPL-2.0-or-later

namespace ArkPar.Engine;

/// <summary>How an engine task ended, in the terms the screens show.</summary>
public enum EngineOutcome
{
    /// <summary>Created, listed, verified complete, or repaired.</summary>
    Completed,

    /// <summary>Stopped by the user before it finished.</summary>
    Canceled,

    /// <summary>Some photos are damaged, missing or renamed, and the recovery set can fix them.</summary>
    RepairPossible,

    /// <summary>Some photos are damaged or missing, and the recovery set cannot fix them all.</summary>
    RepairImpossible,

    /// <summary>The engine could not do the task (bad input, unreadable file, out of memory...).</summary>
    Error,
}

internal static class EngineOutcomes
{
    // Exit code bits, documented in source/par2j/Command_par2j.txt and set in repair.c / par2.c.
    private const int FatalError = 1;
    private const int CanceledByKey = 2;
    private const int InputFilesIncomplete = 4;
    private const int RepairSucceeded = 16;
    private const int RenamePossible = 32;
    private const int RejoinPossible = 64;
    private const int RepairablePossible = 128;
    private const int KnownBits = 0x1FF;

    public static EngineOutcome FromExitCode(int exitCode)
    {
        if (exitCode < 0 || (exitCode & ~KnownBits) != 0 || (exitCode & FatalError) != 0)
        {
            return EngineOutcome.Error;
        }

        if ((exitCode & CanceledByKey) != 0)
        {
            return EngineOutcome.Canceled;
        }

        if ((exitCode & InputFilesIncomplete) == 0)
        {
            // 0, 16 (repaired) and 256 (a recovery file is damaged, but every photo is complete).
            return EngineOutcome.Completed;
        }

        if ((exitCode & RepairSucceeded) != 0)
        {
            return EngineOutcome.RepairImpossible; // 16 | 4: the repair ran and failed.
        }

        // 128 | 8 | 4 means "just enough recovery data, may fail": the engine tries it, so the screen offers it.
        return (exitCode & (RenamePossible | RejoinPossible | RepairablePossible)) != 0
            ? EngineOutcome.RepairPossible
            : EngineOutcome.RepairImpossible;
    }
}
