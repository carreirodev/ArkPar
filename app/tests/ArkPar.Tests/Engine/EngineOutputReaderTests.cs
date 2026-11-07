// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using ArkPar.Engine;

namespace ArkPar.Tests.Engine;

[TestClass]
public sealed class EngineOutputReaderTests
{
    [TestMethod]
    public void CreateReportsEachProgressUpdateWithItsActivity()
    {
        var progress = Read("create.txt");

        CollectionAssert.AreEqual(
            new[] { new EngineProgress("Creating recovery slice", 0.0), new EngineProgress("Creating recovery slice", 100.0) },
            progress.ToArray());
    }

    [TestMethod]
    public void RepairReportsBothRepairActivitiesAndIgnoresFileCounters()
    {
        // repair.txt also has counters such as "120 \r" and "159 \r" while files are scanned: those are not percentages.
        var progress = Read("repair.txt");

        CollectionAssert.AreEqual(
            new[]
            {
                new EngineProgress("Computing matrix", 0.0),
                new EngineProgress("Computing matrix", 100.0),
                new EngineProgress("Recovering slice", 0.0),
                new EngineProgress("Recovering slice", 100.0),
            },
            progress.ToArray());
    }

    [TestMethod]
    public void CanceledRunReportsProgressUpToTheCancelAndNothingForPauseOrCancel()
    {
        var progress = Read("create-canceled.txt");

        Assert.AreEqual(new EngineProgress("Creating recovery slice", 28.5), progress[^1]);
        Assert.HasCount(2, progress);
    }

    [TestMethod]
    public void VerifyWithoutPercentagesReportsNothing()
    {
        Assert.IsEmpty(Read("verify-damaged.txt"));
    }

    [TestMethod]
    public void ProgressIsReportedAsSoonAsItsCarriageReturnArrives()
    {
        // The engine ends each update with a bare CR and then works for a while: the update must not wait for a line end.
        var progress = new RecordedProgress();
        var reader = new EngineOutputReader(progress);

        reader.Write("  0.0% : Creating recovery slice\r  7.3%\r"u8);

        CollectionAssert.AreEqual(
            new[] { new EngineProgress("Creating recovery slice", 0.0), new EngineProgress("Creating recovery slice", 7.3) },
            progress.Reports.ToArray());
    }

    [TestMethod]
    public void FileActivityIsTheFileNameWithoutQuotes()
    {
        // Format of print_progress_file in source/par2j/common2.c: the first update for a file names it, in quotes.
        var progress = new RecordedProgress();
        var reader = new EngineOutputReader(progress);

        reader.Write(Encoding.UTF8.GetBytes("  0.0% : \"viagem\\ônibus.jpg\"\r 42.5%\r"));

        Assert.AreEqual(new EngineProgress("viagem\\ônibus.jpg", 42.5), progress.Reports[^1]);
    }

    [TestMethod]
    public void TextKeepsTheWholeOutputEvenWhenBytesArriveOneByOne()
    {
        // One byte at a time splits the two-byte UTF-8 letters of "família" and "ônibus".
        var bytes = Fixture.Bytes("verify-damaged.txt");
        var reader = new EngineOutputReader(progress: null);

        foreach (var b in bytes)
        {
            reader.Write([b]);
        }

        Assert.AreEqual(Encoding.UTF8.GetString(bytes), reader.Text);
        Assert.Contains("\"P:\\Fotos de família\\fotos.par2\"", reader.Text);
    }

    private static IReadOnlyList<EngineProgress> Read(string fixture)
    {
        var progress = new RecordedProgress();
        var reader = new EngineOutputReader(progress);
        foreach (var chunk in Fixture.Bytes(fixture).Chunk(7))
        {
            reader.Write(chunk);
        }

        return progress.Reports;
    }
}
