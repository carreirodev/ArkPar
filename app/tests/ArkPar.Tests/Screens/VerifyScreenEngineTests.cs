// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;
using ArkPar.Screens;
using ArkPar.Tests.Engine;

namespace ArkPar.Tests.Screens;

/// <summary>The Verify and repair screen over the real engine.</summary>
[TestClass]
public sealed class VerifyScreenEngineTests(TestContext testContext)
{
    private string _workDir = "";
    private string _folder = "";

    [TestInitialize]
    public void CreateFolder()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ArkPar.Tests", Guid.NewGuid().ToString("N"));
        _folder = Path.Combine(_workDir, "Fotos de família");
        Directory.CreateDirectory(_folder);
    }

    [TestCleanup]
    public void DeleteFolder()
    {
        Directory.Delete(_workDir, recursive: true);
    }

    [TestMethod]
    public async Task MissingPhotoIsFoundThenRepaired()
    {
        var beach = WritePhoto("praia 01.jpg", 1);
        var bus = WritePhoto("ônibus.jpg", 2);
        var original = File.ReadAllBytes(bus);
        var recoveryFile = Path.Combine(_folder, "fotos.par2");
        using (var create = EngineRun.Start(new EngineCommand.Create(recoveryFile, _folder, [beach, bus], RedundancyPercent: 60), enginePath: EngineProcess.ExePath))
        {
            Assert.AreEqual(EngineOutcome.Completed, (await create.Completion.WaitAsync(TimeSpan.FromMinutes(2), testContext.CancellationToken)).Outcome);
        }

        File.Delete(bus);
        using var screen = new VerifyScreen((command, progress) => EngineRun.Start(command, progress, EngineProcess.ExePath));

        await screen.VerifyAsync(recoveryFile).WaitAsync(TimeSpan.FromMinutes(2), testContext.CancellationToken);

        Assert.AreEqual(VerifyStage.Verified, screen.Stage, screen.Details);
        Assert.AreEqual(new FileCounts(Complete: 1, Damaged: 0, Missing: 1, Misnamed: 0), screen.Counts);
        Assert.AreEqual("1 arquivo com problema. O reparo é possível.", screen.Banner?.Title);
        Assert.StartsWith("Precisa de ", screen.Banner?.Detail ?? "");
        Assert.AreEqual(new ProblemFile("ônibus.jpg", ProblemKind.Missing, "Faltando", ImagePath: null, "195 KB"), screen.ProblemFiles.Single());
        Assert.IsTrue(screen.CanRepair);

        await screen.RepairAsync().WaitAsync(TimeSpan.FromMinutes(2), testContext.CancellationToken);

        Assert.AreEqual(VerifyStage.Repaired, screen.Stage, screen.Details);
        Assert.AreEqual(new Banner(BannerKind.Success, "1 arquivo consertado.", Detail: null), screen.Banner);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(bus));
    }

    private string WritePhoto(string name, int seed)
    {
        var bytes = new byte[200_000];
        new Random(seed).NextBytes(bytes);
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
