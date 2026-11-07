// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;

namespace ArkPar.Tests.Engine;

/// <summary>The engine's list (<c>l</c>), run for real on a set it made.</summary>
[TestClass]
public sealed class EngineListingTests(TestContext testContext)
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
    public async Task ListGivesTheFilesASetProtectsWithoutReadingThem()
    {
        var recoveryFile = await CreateSetAsync();

        var listing = await EngineListing.RunAsync(recoveryFile, EngineProcess.ExePath, testContext.CancellationToken);

        Assert.AreEqual(_folder, listing.BaseDirectory);
        Assert.AreEqual(1000, listing.FileSizes[Path.Combine(_folder, "praia 01.jpg")]);
        Assert.AreEqual(3000, listing.FileSizes[Path.Combine(_folder, "viagem", "ônibus.jpg")]);
    }

    [TestMethod]
    public async Task ListFindsTheSetThroughItsRecoveryFilesWhenTheIndexIsGone()
    {
        var recoveryFile = await CreateSetAsync();
        File.Delete(recoveryFile);

        var listing = await EngineListing.RunAsync(recoveryFile, EngineProcess.ExePath, testContext.CancellationToken);

        Assert.HasCount(2, listing.FileSizes);
    }

    [TestMethod]
    public async Task ListOfNoSetFailsWithTheEnginesReason()
    {
        var failure = await Assert.ThrowsExactlyAsync<EngineListingException>(
            () => EngineListing.RunAsync(Path.Combine(_folder, "nenhum.par2"), EngineProcess.ExePath, testContext.CancellationToken));

        Assert.Contains("valid file is not found", failure.Output);
    }

    private async Task<string> CreateSetAsync()
    {
        string[] files = [WritePhoto("praia 01.jpg", 1000), WritePhoto(@"viagem\ônibus.jpg", 3000)];
        var recoveryFile = Path.Combine(_folder, "Fotos de família.par2");
        using var run = EngineRun.Start(new EngineCommand.Create(recoveryFile, _folder, files, RedundancyPercent: 10), enginePath: EngineProcess.ExePath);
        var result = await run.Completion;
        Assert.AreEqual(EngineOutcome.Completed, result.Outcome, result.Output);
        return recoveryFile;
    }

    private string WritePhoto(string name, int size)
    {
        var path = Path.Combine(_folder, name);
        var bytes = new byte[size];
        new Random(name.Length).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
