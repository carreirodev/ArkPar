// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;
using ArkPar.Screens;
using ArkPar.Tests.Engine;

namespace ArkPar.Tests.Screens;

/// <summary>Verifying a folder over the real engine.</summary>
[TestClass]
public sealed class FolderVerifyScreenEngineTests(TestContext testContext)
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);

    private string _workDir = "";
    private string _year = "";

    [TestInitialize]
    public void CreateFolder()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ArkPar.Tests", Guid.NewGuid().ToString("N"));
        _year = Path.Combine(_workDir, "2019");
        Directory.CreateDirectory(_year);
    }

    [TestCleanup]
    public void DeleteFolder()
    {
        Directory.Delete(_workDir, recursive: true);
    }

    [TestMethod]
    public async Task DamagedFolderIsFoundAndRepairedWhileTheOtherStaysIntact()
    {
        var christmas = await CreateSetAsync("Natal", "ceia.jpg", "árvore.jpg");
        await CreateSetAsync("Praia", "praia 01.jpg", "ônibus.jpg");

        // Natal's index is gone: its set is verified from a recovery file. One of Praia's photos is damaged.
        File.Delete(christmas);
        var bus = Path.Combine(_year, "Praia", "ônibus.jpg");
        var original = File.ReadAllBytes(bus);
        using (var stream = File.OpenWrite(bus))
        {
            stream.Write(new byte[5000]);
        }

        using var detail = new VerifyScreen(Start);
        using var screen = new FolderVerifyScreen(Start);
        screen.Follow(detail);

        await screen.ChooseAsync(_year).WaitAsync(Limit, testContext.CancellationToken);
        Assert.AreEqual("2 conjuntos em 2 pastas", screen.FoundText);
        StringAssert.StartsWith(Path.GetFileName(screen.Folders[0].Sets[0].RecoveryFile), "Natal.vol");

        await screen.StartAsync().WaitAsync(Limit, testContext.CancellationToken);

        Assert.AreEqual(new FolderCounts(Complete: 1, Repairable: 1, Unrepairable: 0, Unverified: 0), screen.Counts, screen.Details);
        Assert.AreEqual("2 arquivos, todos íntegros", screen.Folders[0].HealthText);
        Assert.AreEqual("1 arquivo com problema · o reparo é possível", screen.Folders[1].HealthText);

        await screen.OpenAsync(screen.Folders[1].Sets[0]).WaitAsync(Limit, testContext.CancellationToken);
        Assert.AreEqual(new ProblemFile("ônibus.jpg", ProblemKind.Damaged, "Danificado", bus, detail.ProblemFiles.Single().Meta), detail.ProblemFiles.Single());
        screen.CloseDetail();

        await screen.RepairAllAsync().WaitAsync(Limit, testContext.CancellationToken);

        Assert.AreEqual(new FolderCounts(Complete: 2, Repairable: 0, Unrepairable: 0, Unverified: 0), screen.Counts, screen.Details);
        Assert.AreEqual(new Banner(BannerKind.Success, "As 2 pastas estão íntegras.", Detail: null), screen.Banner);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(bus));
    }

    private static EngineRun Start(EngineCommand command, IProgress<EngineProgress> progress) => EngineRun.Start(command, progress, EngineProcess.ExePath);

    /// <summary>Creates a set of two photos in a folder of the year; returns its index file.</summary>
    private async Task<string> CreateSetAsync(string folderName, params string[] photos)
    {
        var folder = Path.Combine(_year, folderName);
        Directory.CreateDirectory(folder);
        var files = photos.Select((name, seed) =>
        {
            var bytes = new byte[200_000];
            new Random(folderName.Length * 10 + seed).NextBytes(bytes);
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }).ToList();

        var recoveryFile = Path.Combine(folder, folderName + ".par2");
        using var create = EngineRun.Start(new EngineCommand.Create(recoveryFile, folder, files, RedundancyPercent: 60), enginePath: EngineProcess.ExePath);
        Assert.AreEqual(EngineOutcome.Completed, (await create.Completion.WaitAsync(Limit, testContext.CancellationToken)).Outcome);
        return recoveryFile;
    }
}
