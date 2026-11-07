// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;

namespace ArkPar.Tests.Engine;

/// <summary>The engine's trial (<c>t</c>), run for real.</summary>
[TestClass]
public sealed class EngineTrialTests(TestContext testContext)
{
    private string _folder = "";

    [TestInitialize]
    public void CreateFolder()
    {
        _folder = Path.Combine(Path.GetTempPath(), "ArkPar.Tests", Guid.NewGuid().ToString("N"), "Fotos de família");
        Directory.CreateDirectory(Path.Combine(_folder, "viagem"));
    }

    [TestCleanup]
    public void DeleteFolder() => Directory.Delete(Path.GetDirectoryName(_folder)!, recursive: true);

    [TestMethod]
    public async Task TrialWorksOutTheRecoveryFilesWithoutWritingThem()
    {
        var create = Create(WritePhoto("praia 01.jpg", 1_000_000), WritePhoto(@"viagem\ônibus.jpg", 1_000_000));

        var estimate = await EngineTrial.RunAsync(create, EngineProcess.ExePath, testContext.CancellationToken);

        Assert.AreEqual(1, estimate.RecoveryFiles);
        Assert.IsGreaterThan(0, estimate.RecoveryBlocks);
        Assert.AreEqual(estimate.SourceBlocks / 10, estimate.RecoveryBlocks, 1, "10% of the blocks.");
        Assert.IsGreaterThan(estimate.RepairableSize, estimate.TotalSize, "The files also hold the index and checksums.");
        Assert.IsEmpty(Directory.GetFiles(_folder, "*.par2"));
    }

    [TestMethod]
    public async Task TrialOfAFileThatIsGoneFailsWithTheEnginesReason()
    {
        var create = Create(WritePhoto("praia 01.jpg", 1000), Path.Combine(_folder, "sumiu.jpg"));

        var failure = await Assert.ThrowsExactlyAsync<EngineTrialException>(() => EngineTrial.RunAsync(create, EngineProcess.ExePath, testContext.CancellationToken));

        Assert.Contains("input file is not found", failure.Output);
    }

    [TestMethod]
    public async Task CanceledTrialStops()
    {
        var create = Create(WritePhoto("praia 01.jpg", 1000));

        await Assert.ThrowsAsync<OperationCanceledException>(() => EngineTrial.RunAsync(create, EngineProcess.ExePath, new CancellationToken(canceled: true)));
    }

    private EngineCommand.Create Create(params string[] files) =>
        new(Path.Combine(_folder, "Fotos de família.par2"), _folder, files, RedundancyPercent: 10);

    private string WritePhoto(string name, int size)
    {
        var path = Path.Combine(_folder, name);
        var bytes = new byte[size];
        new Random(name.Length).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
