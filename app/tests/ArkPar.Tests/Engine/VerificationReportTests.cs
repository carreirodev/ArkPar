// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;

namespace ArkPar.Tests.Engine;

[TestClass]
public sealed class VerificationReportTests
{
    private const string Folder = @"P:\Fotos de família";
    private const string Beach = @"P:\Fotos de família\praia 01.jpg";
    private const string Birthday = @"P:\Fotos de família\aniversário.jpg";
    private const string Bus = @"P:\Fotos de família\viagem\ônibus.jpg";

    [TestMethod]
    public void CompleteSetListsEveryPhotoAsComplete()
    {
        var report = Load("verify-complete");

        Assert.AreEqual(Folder, report.BaseDirectory);
        CollectionAssert.AreEquivalent(new[] { Beach, Birthday, Bus }, report.CompleteFiles.ToArray());
        Assert.IsEmpty(report.DamagedFiles);
        Assert.IsEmpty(report.MissingFiles);
        Assert.IsEmpty(report.MisnamedFiles);
        Assert.IsEmpty(report.FoundFiles);
    }

    [TestMethod]
    public void DamagedSetSortsEachPhotoByWhatHappenedToIt()
    {
        var report = Load("verify-damaged");

        CollectionAssert.AreEqual(new[] { Beach }, report.DamagedFiles.ToArray());
        CollectionAssert.AreEqual(new[] { Bus }, report.MissingFiles.ToArray());
        Assert.HasCount(1, report.MisnamedFiles);
        Assert.AreEqual(@"P:\Fotos de família\aniversario renomeado.jpg", report.MisnamedFiles[Birthday]);
        Assert.IsEmpty(report.CompleteFiles);
        CollectionAssert.AreEquivalent(new[] { Beach, Birthday, Bus }, report.SourceFiles.ToArray());
    }

    [TestMethod]
    public void PhotosNotMissingOrDamagedAreComplete()
    {
        var report = Load("verify-impossible");

        CollectionAssert.AreEquivalent(new[] { Beach, Birthday }, report.MissingFiles.ToArray());
        CollectionAssert.AreEqual(new[] { Bus }, report.CompleteFiles.ToArray());
    }

    [TestMethod]
    public void SetAtTheRootOfADriveHasTheRootAsItsFolder()
    {
        // The engine writes "BaseDirectory":"P:", which on its own means P:'s current folder, not its root.
        var report = VerificationReport.Load(Fixture.PathOf("verify-root.json"), engineOutput: "");

        Assert.AreEqual(@"P:\", report.BaseDirectory);
        Assert.AreEqual(@"P:\praia 01.jpg", report.CompleteFiles.Single());
    }

    [TestMethod]
    public void FilesHoldingPartsOfAPhotoAreFound()
    {
        var report = VerificationReport.Load(Fixture.PathOf("verify-found.json"), engineOutput: "");

        Assert.AreEqual(@"P:\Fotos de família\praia 01 - cópia.jpg", report.FoundFiles.Single());
        CollectionAssert.AreEqual(new[] { Beach }, report.MissingFiles.ToArray());
    }

    [TestMethod]
    public void RepairableSetNeedsSomeOfTheAvailableRecoveryBlocks()
    {
        var report = Load("verify-damaged");

        // "Recovery Slice found : 120" and "Input File Slice lost : 81".
        Assert.AreEqual(new RecoveryBlocks(Available: 120, Needed: 81), report.RecoveryBlocks);
        Assert.AreEqual(0, report.RecoveryBlocks?.Lacking);
    }

    [TestMethod]
    public void UnrepairableSetLacksRecoveryBlocks()
    {
        var report = Load("verify-impossible");

        // The engine says "Need 40 more slice(s) to repair 2 file(s)".
        Assert.AreEqual(40, report.RecoveryBlocks?.Lacking);
    }

    [TestMethod]
    public void CompleteSetNeedsNoRecoveryBlocks()
    {
        Assert.AreEqual(new RecoveryBlocks(Available: 120, Needed: 0), Load("verify-complete").RecoveryBlocks);
    }

    [TestMethod]
    public void RepairCountsTheBlocksNeededBeforeItRepaired()
    {
        var report = VerificationReport.Load(Fixture.PathOf("repair.json"), File.ReadAllText(Fixture.PathOf("repair.txt")));

        Assert.AreEqual(new RecoveryBlocks(Available: 120, Needed: 81), report.RecoveryBlocks);
    }

    [TestMethod]
    public void OutputWithoutBlockCountsGivesNone()
    {
        Assert.IsNull(VerificationReport.Load(Fixture.PathOf("verify-root.json"), engineOutput: "").RecoveryBlocks);
    }

    [TestMethod]
    public void EveryPhotoHasTheSizeTheSetRecorded()
    {
        var report = Load("verify-damaged");

        // Including the missing one, whose size only the recovery set knows.
        Assert.AreEqual(2_000_000, report.FileSizes[Bus]);
        Assert.AreEqual(2_000_000, report.FileSizes[Birthday]);
        Assert.HasCount(3, report.FileSizes);
    }

    private static VerificationReport Load(string fixture) =>
        VerificationReport.Load(Fixture.PathOf(fixture + ".json"), File.ReadAllText(Fixture.PathOf(fixture + ".txt")));
}
