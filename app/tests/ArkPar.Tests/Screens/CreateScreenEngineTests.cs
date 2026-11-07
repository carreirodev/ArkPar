// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;
using ArkPar.Screens;
using ArkPar.Tests.Engine;

namespace ArkPar.Tests.Screens;

/// <summary>The Create screen over the real engine.</summary>
[TestClass]
public sealed class CreateScreenEngineTests(TestContext testContext)
{
    private string _workDir = "";
    private string _folder = "";

    [TestInitialize]
    public void CreateFolder()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ArkPar.Tests", Guid.NewGuid().ToString("N"));
        _folder = Path.Combine(_workDir, "Fotos de família");
        Directory.CreateDirectory(Path.Combine(_folder, "viagem", "2019"));
    }

    [TestCleanup]
    public void DeleteFolder() => Directory.Delete(_workDir, recursive: true);

    [TestMethod]
    public async Task DroppedFolderIsProtectedWithItsSubfoldersAndVerifiesComplete()
    {
        WritePhoto("praia 01.jpg", 1);
        WritePhoto(@"viagem\ônibus.jpg", 2);
        WritePhoto(@"viagem\2019\aniversário.jpg", 3);
        using var screen = NewScreen(StartEngine);

        await screen.AddAsync([_folder]);
        await WaitUntilAsync(() => screen.Estimate is not null, "the estimate");

        Assert.AreEqual("3 arquivos · 586 KB", screen.Summary);
        Assert.IsTrue(screen.CanCreate, screen.EstimateProblem);
        var estimate = screen.Estimate!;

        await screen.CreateAsync().WaitAsync(TimeSpan.FromMinutes(2), testContext.CancellationToken);

        Assert.AreEqual(CreateStage.Created, screen.Stage, screen.Details);
        var recoveryFile = Path.Combine(_folder, "Fotos de família.par2");
        var made = Selection.RecoverySetFiles(recoveryFile);
        Assert.AreEqual(estimate.TotalSize, made.Sum(file => new FileInfo(file).Length), "The estimate is what the engine made.");
        Assert.HasCount(estimate.RecoveryFiles + 1, made);

        using var verify = new VerifyScreen((command, progress) => EngineRun.Start(command, progress, EngineProcess.ExePath));
        await verify.VerifyAsync(recoveryFile).WaitAsync(TimeSpan.FromMinutes(2), testContext.CancellationToken);

        Assert.AreEqual(VerifyStage.Verified, verify.Stage, verify.Details);
        Assert.AreEqual(new FileCounts(Complete: 3, Damaged: 0, Missing: 0, Misnamed: 0), verify.Counts);
        Assert.AreEqual(@"praia 01.jpg, viagem\2019\aniversário.jpg, viagem\ônibus.jpg", string.Join(", ", verify.CompleteFiles.Select(file => file.Name).Order(StringComparer.Ordinal)));
    }

    [TestMethod]
    public async Task PerFolderMakesASetInEachFolderThatVerifiesItsOwnPhotos()
    {
        WritePhoto("praia 01.jpg", 1);
        WritePhoto(@"viagem\ônibus.jpg", 2);
        WritePhoto(@"viagem\2019\aniversário.jpg", 3);
        WritePhoto(@"viagem\2019\bolo.jpg", 4);
        using var screen = NewScreen(StartEngine);

        await screen.AddAsync([_folder]);
        screen.Mode = CreationMode.PerFolder;
        await WaitUntilAsync(() => screen.Estimate is not null, "the estimate");

        Assert.IsTrue(screen.CanCreate, screen.EstimateProblem);
        Assert.AreEqual("3 conjuntos serão criados", screen.SetsTitle);
        var estimate = screen.Estimate!;

        await screen.CreateAsync().WaitAsync(TimeSpan.FromMinutes(2), testContext.CancellationToken);

        Assert.AreEqual(CreateStage.Created, screen.Stage, screen.Details);
        var sets = new (string RecoveryFile, string Photos)[]
        {
            (Path.Combine(_folder, "Fotos de família.par2"), "praia 01.jpg"),
            (Path.Combine(_folder, "viagem", "viagem.par2"), "ônibus.jpg"),
            (Path.Combine(_folder, "viagem", "2019", "2019.par2"), "aniversário.jpg, bolo.jpg"),
        };
        Assert.AreEqual(estimate.TotalSize, sets.Sum(set => Selection.RecoverySetFiles(set.RecoveryFile).Sum(file => new FileInfo(file).Length)), "The estimate is what the engine made.");
        foreach (var (recoveryFile, photos) in sets)
        {
            using var verify = new VerifyScreen((command, progress) => EngineRun.Start(command, progress, EngineProcess.ExePath));
            await verify.VerifyAsync(recoveryFile).WaitAsync(TimeSpan.FromMinutes(2), testContext.CancellationToken);

            Assert.AreEqual(VerifyStage.Verified, verify.Stage, verify.Details);
            Assert.AreEqual(photos, string.Join(", ", verify.CompleteFiles.Select(file => file.Name).Order(StringComparer.Ordinal)), recoveryFile);
            Assert.AreEqual(new FileCounts(Complete: photos.Split(", ").Length, Damaged: 0, Missing: 0, Misnamed: 0), verify.Counts, recoveryFile);
        }
    }

    [TestMethod]
    public async Task CanceledCreateLeavesOnlyThePhotos()
    {
        using var screen = await StartSlowCreateAsync();

        screen.Cancel();
        await WaitUntilAsync(() => screen.Stage == CreateStage.Canceled, "the cancel");

        CollectionAssert.AreEqual(new[] { Path.Combine(_folder, "grande.jpg") }, Directory.GetFiles(_folder));
    }

    [TestMethod]
    public async Task ClosingTheWindowMidCreateLeavesOnlyThePhotos()
    {
        var screen = await StartSlowCreateAsync();

        screen.Dispose();

        CollectionAssert.AreEqual(new[] { Path.Combine(_folder, "grande.jpg") }, Directory.GetFiles(_folder));
    }

    /// <summary>A create that takes several seconds: 100 MB, 100% redundancy, one CPU thread and no GPU, under way.</summary>
    private async Task<CreateScreen> StartSlowCreateAsync()
    {
        WritePhoto("grande.jpg", 3, size: 100_000_000);
        var screen = NewScreen((command, progress) =>
        {
            var create = (EngineCommand.Create)command;
            return EngineRun.Start(
                create,
                runDirectory => [.. EngineArguments.For(create, runDirectory).Select(argument => argument == EngineArguments.CpuAndGpu ? "/lc1" : argument)],
                progress,
                EngineProcess.ExePath,
                EngineRun.DefaultCancelGracePeriod);
        });
        await screen.AddAsync([_folder]);
        screen.Redundancy = 100;
        await WaitUntilAsync(() => screen.CanCreate, "the estimate");

        _ = screen.CreateAsync();
        await WaitUntilAsync(() => screen.ActivityText == "Criando os blocos de recuperação" && screen.Percent > 0, "the calculation to start");
        Assert.IsNotEmpty(Directory.GetFiles(_folder, "*.par2"), "The engine is writing the recovery files.");
        return screen;
    }

    private static CreateScreen NewScreen(Func<EngineCommand, IProgress<EngineProgress>, IEngineRun> startEngine) =>
        new(startEngine, (create, stop) => EngineTrial.RunAsync(create, EngineProcess.ExePath, stop), files => Array.ForEach([.. files], File.Delete));

    private static IEngineRun StartEngine(EngineCommand command, IProgress<EngineProgress> progress) => EngineRun.Start(command, progress, EngineProcess.ExePath);

    private async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!condition())
        {
            Assert.IsLessThan(deadline, DateTime.UtcNow, $"Timed out waiting for {what}.");
            await Task.Delay(50, testContext.CancellationToken);
        }
    }

    private void WritePhoto(string name, int seed, int size = 200_000)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(_folder, name), bytes);
    }
}
