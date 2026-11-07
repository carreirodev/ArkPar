// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using ArkPar.Engine;

namespace ArkPar.Tests.Engine;

/// <summary>The sizes a trial (<c>t</c>) works out, read from captured real output (Engine/Fixtures/README.md).</summary>
[TestClass]
public sealed class RecoveryEstimateTests
{
    [TestMethod]
    public void TrialGivesTheBlocksAndTheSizeOfTheRecoveryFiles()
    {
        var estimate = RecoveryEstimate.Parse(Output("trial.txt"));

        // Three 2,000,000-byte photos at 10%: 240 blocks of 25,000 bytes, 24 of them for recovery.
        Assert.AreEqual(new RecoveryEstimate(BlockSize: 25000, SourceBlocks: 240, RecoveryBlocks: 24, RecoveryFiles: 1, TotalSize: 635216), estimate);
        Assert.AreEqual(600000, estimate.RepairableSize, "24 blocks of 25,000 bytes: up to 600,000 bytes of lost data.");
    }

    [TestMethod]
    public void TrialReflectsTheAdvancedOptions()
    {
        var estimate = RecoveryEstimate.Parse(Output("trial-advanced.txt"));

        Assert.AreEqual(new RecoveryEstimate(BlockSize: 65536, SourceBlocks: 93, RecoveryBlocks: 9, RecoveryFiles: 3, TotalSize: 609164), estimate);
    }

    [TestMethod]
    public void OutputWithoutTheSizesIsNotAnEstimate()
    {
        Assert.ThrowsExactly<FormatException>(() => RecoveryEstimate.Parse(Output("trial-missing-file.txt")));
    }

    private static string Output(string fixture) => Encoding.UTF8.GetString(Fixture.Bytes(fixture));
}
