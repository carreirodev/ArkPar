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
        var report = VerificationReport.Load(Fixture.PathOf("verify-complete.json"));

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
        var report = VerificationReport.Load(Fixture.PathOf("verify-damaged.json"));

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
        var report = VerificationReport.Load(Fixture.PathOf("verify-impossible.json"));

        CollectionAssert.AreEquivalent(new[] { Beach, Birthday }, report.MissingFiles.ToArray());
        CollectionAssert.AreEqual(new[] { Bus }, report.CompleteFiles.ToArray());
    }

    [TestMethod]
    public void SetAtTheRootOfADriveHasTheRootAsItsFolder()
    {
        // The engine writes "BaseDirectory":"P:", which on its own means P:'s current folder, not its root.
        var report = VerificationReport.Load(Fixture.PathOf("verify-root.json"));

        Assert.AreEqual(@"P:\", report.BaseDirectory);
        Assert.AreEqual(@"P:\praia 01.jpg", report.CompleteFiles.Single());
    }

    [TestMethod]
    public void FilesHoldingPartsOfAPhotoAreFound()
    {
        var report = VerificationReport.Load(Fixture.PathOf("verify-found.json"));

        Assert.AreEqual(@"P:\Fotos de família\praia 01 - cópia.jpg", report.FoundFiles.Single());
        CollectionAssert.AreEqual(new[] { Beach }, report.MissingFiles.ToArray());
    }
}
