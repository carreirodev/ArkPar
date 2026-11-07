// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;
using ArkPar.Engine.Native;

namespace ArkPar.Tests.Engine;

[TestClass]
public sealed class EngineArgumentsTests
{
    private const string RunDirectory = @"C:\Temp\ArkPar\run1";
    private const string Folder = @"P:\Fotos de família";
    private const string RecoveryFile = @"P:\Fotos de família\fotos.par2";

    [TestMethod]
    public void CreateReadsThePhotosFromAUtf8ListAndTurnsTheGpuOn()
    {
        var create = new EngineCommand.Create(RecoveryFile, Folder, [@"P:\Fotos de família\praia 01.jpg"], RedundancyPercent: 10);

        CollectionAssert.AreEqual(
            new[] { "c", "/uo", "/lc256", "/rr10", @"/dP:\Fotos de família", "/fu", RecoveryFile, @"C:\Temp\ArkPar\run1\files.txt" },
            EngineArguments.For(create, RunDirectory).ToArray());
    }

    [TestMethod]
    [DataRow(0.5, "/rr0.5")]
    [DataRow(3.14, "/rr3.14")]
    [DataRow(100, "/rr100")]
    public void RedundancyKeepsUpToTwoDecimalsWithADot(double percent, string expected)
    {
        var create = new EngineCommand.Create(RecoveryFile, Folder, [], (decimal)percent);

        Assert.AreEqual(expected, EngineArguments.For(create, RunDirectory)[3]);
    }

    [TestMethod]
    public void AdvancedOptionsSetBlockSizeRecoveryFileCountAndMemory()
    {
        var create = new EngineCommand.Create(RecoveryFile, Folder, [], 10, new CreateOptions(BlockSize: 65536, RecoveryFileCount: 3, MemoryEighths: 4));

        CollectionAssert.AreEqual(
            new[] { "c", "/uo", "/lc256", "/rr10", "/ss65536", "/rf3", "/m4", @"/dP:\Fotos de família", "/fu", RecoveryFile, @"C:\Temp\ArkPar\run1\files.txt" },
            EngineArguments.For(create, RunDirectory).ToArray());
    }

    [TestMethod]
    public void TrialTakesExactlyTheArgumentsOfTheCreate()
    {
        // The estimate is a dry run of the very create the button starts, so the two never disagree.
        var create = new EngineCommand.Create(RecoveryFile, Folder, [], 10, new CreateOptions(BlockSize: 65536));

        string[] expected = ["t", .. EngineArguments.For(create, RunDirectory).Skip(1)];

        CollectionAssert.AreEqual(expected, EngineArguments.ForTrial(create, RunDirectory).ToArray());
    }

    [TestMethod]
    public void VerifyAndRepairWriteTheirJsonResultInTheRunFolder()
    {
        string[] options = ["/uo", "/lc256", @"/vdC:\Temp\ArkPar\run1", "/w"];
        string[] verify = ["v", .. options, RecoveryFile];

        // A repair sends each damaged photo it replaces to the Recycle Bin, so a failed repair loses nothing.
        string[] repair = ["r", .. options, "/br", RecoveryFile];

        CollectionAssert.AreEqual(verify, EngineArguments.For(new EngineCommand.Verify(RecoveryFile), RunDirectory).ToArray());
        CollectionAssert.AreEqual(repair, EngineArguments.For(new EngineCommand.Repair(RecoveryFile), RunDirectory).ToArray());
        Assert.AreEqual(@"C:\Temp\ArkPar\run1\fotos.par2.json", EngineArguments.ReportPath(RecoveryFile, RunDirectory));
    }

    [TestMethod]
    public void ListTakesNoGpuOption()
    {
        // "l(ist) [uo,h] <par file>" in source/par2j/Command_par2j.txt.
        CollectionAssert.AreEqual(
            new[] { "l", "/uo", RecoveryFile },
            EngineArguments.For(new EngineCommand.List(RecoveryFile), RunDirectory).ToArray());
    }

    [TestMethod]
    public void SourceListHasOnePhotoPerLineWithoutByteOrderMark()
    {
        var create = new EngineCommand.Create(RecoveryFile, Folder, [@"P:\Fotos de família\praia 01.jpg", @"P:\Fotos de família\viagem\ônibus.jpg"], 10);

        var bytes = EngineArguments.SourceList(create);

        CollectionAssert.AreEqual(
            "P:\\Fotos de família\\praia 01.jpg\r\nP:\\Fotos de família\\viagem\\ônibus.jpg\r\n"u8.ToArray(),
            bytes);
    }

    [TestMethod]
    [DataRow("c", "c")]
    [DataRow("", "\"\"")]
    [DataRow(@"/dD:\", @"/dD:\")]
    [DataRow(@"P:\Fotos de família\fotos.par2", @"""P:\Fotos de família\fotos.par2""")]
    [DataRow(@"/dP:\Fotos de família\", @"""/dP:\Fotos de família\\""")]
    [DataRow(@"a""b", @"""a\""b""")]
    [DataRow(@"a\""b", @"""a\\\""b""")]
    public void QuotesOnlyWhatTheCRuntimeWouldSplitOrUnescape(string argument, string expected)
    {
        Assert.AreEqual(expected, CommandLine.Quote(argument));
    }
}
