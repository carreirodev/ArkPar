// SPDX-License-Identifier: GPL-2.0-or-later

namespace ArkPar.Engine;

/// <summary>A task for the engine. Every path is a full Windows path.</summary>
public abstract record EngineCommand
{
    private EngineCommand(string recoveryFile)
    {
        RecoveryFile = recoveryFile;
    }

    /// <summary>The index recovery file of the recovery set (e.g. <c>fotos.par2</c>).</summary>
    public string RecoveryFile { get; }

    /// <summary>Creates a recovery set protecting <paramref name="SourceFiles"/>, all inside <paramref name="BaseDirectory"/>.</summary>
    /// <param name="RedundancyPercent">From 0.01 to 1000, with up to two decimals.</param>
    public sealed record Create(string RecoveryFile, string BaseDirectory, IReadOnlyList<string> SourceFiles, decimal RedundancyPercent)
        : EngineCommand(RecoveryFile);

    /// <summary>Checks every photo of a recovery set without changing anything.</summary>
    public sealed record Verify(string RecoveryFile) : EngineCommand(RecoveryFile);

    /// <summary>Checks every photo of a recovery set and repairs the ones it can.</summary>
    public sealed record Repair(string RecoveryFile) : EngineCommand(RecoveryFile);

    /// <summary>Lists the photos a recovery set protects, without checking them.</summary>
    public sealed record List(string RecoveryFile) : EngineCommand(RecoveryFile);
}
