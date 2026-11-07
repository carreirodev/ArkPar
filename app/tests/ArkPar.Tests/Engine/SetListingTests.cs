// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;

namespace ArkPar.Tests.Engine;

/// <summary>Reading what the engine's list (<c>l</c>) prints, from a captured real run.</summary>
[TestClass]
public sealed class SetListingTests
{
    [TestMethod]
    public void TheListGivesEachFileInFullWithItsRecordedSize()
    {
        var listing = SetListing.Parse(Fixture.Read("list.txt"));

        Assert.AreEqual(@"P:\Fotos de família", listing.BaseDirectory);
        Assert.AreEqual(
            @"P:\Fotos de família\aniversário.jpg; P:\Fotos de família\praia 01.jpg; P:\Fotos de família\viagem\ônibus.jpg",
            string.Join("; ", listing.FileSizes.Keys.Order(StringComparer.Ordinal)));
        Assert.IsTrue(listing.FileSizes.Values.All(size => size == 2_000_000));
        Assert.IsTrue(listing.FileSizes.ContainsKey(@"p:\fotos de família\PRAIA 01.JPG"), "Windows paths ignore case.");
    }

    [TestMethod]
    public void AListThatNamesFewerFilesThanTheSetHasIsRefused()
    {
        var output = Fixture.Read("list.txt").Replace("Input File total count\t: 3", "Input File total count\t: 4", StringComparison.Ordinal);

        Assert.ThrowsExactly<FormatException>(() => SetListing.Parse(output));
    }

    [TestMethod]
    public void AnOutputWithoutTheBaseFolderIsRefused()
    {
        Assert.ThrowsExactly<FormatException>(() => SetListing.Parse("valid file is not found\r\n"));
    }

    [TestMethod]
    public void ASetAtTheRootOfADriveKeepsTheRoot()
    {
        var output = Fixture.Read("list.txt")
            .Replace("Base Directory\t: \"P:\\Fotos de família\\\"", "Base Directory\t: \"P:\\\"", StringComparison.Ordinal);

        var listing = SetListing.Parse(output);

        Assert.AreEqual(@"P:\", listing.BaseDirectory);
        Assert.IsTrue(listing.FileSizes.ContainsKey(@"P:\praia 01.jpg"));
    }
}
