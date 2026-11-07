// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;

namespace ArkPar.Screens;

/// <summary>What a create will make, over all its sets, as the engine's trial of each set works it out.</summary>
public sealed class CreationEstimate
{
    /// <param name="sets">The sets of the create.</param>
    /// <param name="estimates">The engine's estimate of each of <paramref name="sets"/>, in the same order.</param>
    public CreationEstimate(IReadOnlyList<PlannedSet> sets, IReadOnlyList<RecoveryEstimate> estimates)
    {
        if (sets.Count == 0 || sets.Count != estimates.Count)
        {
            throw new ArgumentException("One estimate for each set, and at least one set.", nameof(estimates));
        }

        Sets = estimates;
        TotalSize = estimates.Sum(estimate => estimate.TotalSize);
        RecoveryFiles = estimates.Sum(estimate => estimate.RecoveryFiles);
        SourceBlocks = estimates.Sum(estimate => estimate.SourceBlocks);
        RecoveryBlocks = estimates.Sum(estimate => estimate.RecoveryBlocks);

        // A set of empty files (0 bytes) has nothing to lose, so it leaves the least share to the others.
        LeastRepairablePercent = sets.Zip(estimates)
            .Where(pair => pair.First.TotalSize > 0)
            .Select(pair => Math.Min(100, 100.0 * pair.Second.RepairableSize / pair.First.TotalSize))
            .DefaultIfEmpty(100)
            .Min();
    }

    /// <summary>The engine's estimate of each set.</summary>
    public IReadOnlyList<RecoveryEstimate> Sets { get; }

    /// <summary>The size of all the recovery files together, the indexes included, in bytes.</summary>
    public long TotalSize { get; }

    /// <summary>How many recovery files hold the recovery blocks, the indexes aside.</summary>
    public int RecoveryFiles { get; }

    /// <summary>How many <c>.par2</c> files will appear: each set's recovery files and its index.</summary>
    public int Par2Files => RecoveryFiles + Sets.Count;

    public int SourceBlocks { get; }

    public int RecoveryBlocks { get; }

    /// <summary>
    /// The share of its own files that the set rebuilding the least share can rebuild, in percent: each set
    /// repairs only the damage in the files it protects.
    /// </summary>
    public double LeastRepairablePercent { get; }
}
