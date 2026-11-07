// SPDX-License-Identifier: GPL-2.0-or-later

namespace ArkPar.Engine;

/// <summary>What a repair takes from the recovery set, counted in recovery blocks.</summary>
/// <param name="Available">Recovery blocks found intact in the recovery files.</param>
/// <param name="Needed">Recovery blocks the repair uses up: one per block of the photos that was lost.</param>
public sealed record RecoveryBlocks(int Available, int Needed)
{
    /// <summary>How many more recovery blocks the repair would need; 0 when there are enough.</summary>
    public int Lacking => Math.Max(0, Needed - Available);
}
