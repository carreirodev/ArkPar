// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Screens;

namespace ArkPar.Tests.Screens;

[TestClass]
public sealed class SizeTextTests
{
    // Sizes from the approved visual reference (decision Q14), and what Windows Explorer shows.
    [TestMethod]
    [DataRow(6_501_220L, "6,2 MB")]
    [DataRow(190_840_772L, "182 MB")]
    [DataRow(2_000_000L, "1,9 MB")]
    [DataRow(3_113_851_289L, "2,9 GB")]
    [DataRow(10_485_760L, "10 MB")]
    [DataRow(2_048L, "2,0 KB")]
    [DataRow(1_099_511_627_776L, "1,0 TB")]
    public void ShowsOneDecimalBelowTenAndNoneAbove(long bytes, string expected)
    {
        Assert.AreEqual(expected, SizeText.Of(bytes));
    }

    [TestMethod]
    [DataRow(0L, "0 KB")]
    [DataRow(1L, "1 KB")]
    [DataRow(1_023L, "1 KB")]
    public void SmallFilesShowInKilobytesNeverBytes(long bytes, string expected)
    {
        Assert.AreEqual(expected, SizeText.Of(bytes));
    }

    [TestMethod]
    public void RoundingThatReachesTheNextUnitShowsTheNextUnit()
    {
        // 1023.96 KB would round to "1.024 KB".
        Assert.AreEqual("1,0 MB", SizeText.Of(1_048_535L));
    }
}
