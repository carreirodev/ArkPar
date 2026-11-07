// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;

namespace ArkPar.Tests.Engine;

/// <summary>Runs the real engine through <see cref="EngineRun"/>, in a folder with accents and spaces.</summary>
[TestClass]
public sealed class EngineRunTests(TestContext testContext)
{
    private string _folder = "";

    [TestInitialize]
    public void CreateFolder()
    {
        _folder = Path.Combine(Path.GetTempPath(), "ArkPar.Tests", Guid.NewGuid().ToString("N"), "Fotos de família");
        Directory.CreateDirectory(Path.Combine(_folder, "viagem"));
    }

    [TestCleanup]
    public void DeleteFolder()
    {
        Directory.Delete(Path.GetDirectoryName(_folder)!, recursive: true);
    }

    [TestMethod]
    public async Task CreatedSetVerifiesWithEveryPhotoComplete()
    {
        string[] photos = [WritePhoto("praia 01.jpg", 1), WritePhoto(@"viagem\ônibus.jpg", 2)];
        var progress = new RecordedProgress();

        var created = await RunAsync(new EngineCommand.Create(RecoveryFile, _folder, photos, RedundancyPercent: 10), progress);
        Assert.AreEqual(EngineOutcome.Completed, created.Outcome, created.Output);
        Assert.IsNull(created.Report);
        Assert.AreEqual(100.0, progress.Reports[^1].Percent, created.Output);

        var verified = await RunAsync(new EngineCommand.Verify(RecoveryFile));
        Assert.AreEqual(EngineOutcome.Completed, verified.Outcome, verified.Output);
        CollectionAssert.AreEquivalent(photos, verified.Report!.CompleteFiles.ToArray());
    }

    [TestMethod]
    public async Task DamagedPhotoIsReportedThenRepaired()
    {
        var photo = WritePhoto("praia 01.jpg", 1);
        var original = File.ReadAllBytes(photo);
        await RunAsync(new EngineCommand.Create(RecoveryFile, _folder, [photo], RedundancyPercent: 10));
        var damaged = original.ToArray();
        damaged[100] ^= 0xFF;
        File.WriteAllBytes(photo, damaged);

        var verified = await RunAsync(new EngineCommand.Verify(RecoveryFile));
        Assert.AreEqual(EngineOutcome.RepairPossible, verified.Outcome, verified.Output);
        Assert.AreEqual(photo, verified.Report!.DamagedFiles.Single());

        var repaired = await RunAsync(new EngineCommand.Repair(RecoveryFile));
        Assert.AreEqual(EngineOutcome.Completed, repaired.Outcome, repaired.Output);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(photo));
    }

    [TestMethod]
    public async Task ListNamesThePhotosOfTheSet()
    {
        var photo = WritePhoto(@"viagem\ônibus.jpg", 2);
        await RunAsync(new EngineCommand.Create(RecoveryFile, _folder, [photo], RedundancyPercent: 10));

        var listed = await RunAsync(new EngineCommand.List(RecoveryFile));

        Assert.AreEqual(EngineOutcome.Completed, listed.Outcome, listed.Output);
        Assert.Contains("\"viagem/ônibus.jpg\"", listed.Output);
    }

    [TestMethod]
    public async Task MissingRecoveryFileIsAnError()
    {
        var verified = await RunAsync(new EngineCommand.Verify(Path.Combine(_folder, "nada.par2")));

        Assert.AreEqual(EngineOutcome.Error, verified.Outcome, verified.Output);
        Assert.IsNull(verified.Report);
    }

    [TestMethod]
    public async Task PauseHoldsTheTaskUntilResume()
    {
        var progress = new RecordedProgress();
        using var run = StartSlowCreate(progress);
        await WaitForCalculationAsync(progress);

        run.Pause();
        Assert.IsTrue(run.IsPaused);
        // The update in flight when the key arrives may still come; after that, nothing.
        await Task.Delay(1500, testContext.CancellationToken);
        var reportsWhilePaused = progress.Reports.Count;
        await Task.Delay(2500, testContext.CancellationToken);
        Assert.HasCount(reportsWhilePaused, progress.Reports);
        Assert.IsFalse(run.Completion.IsCompleted);

        run.Resume();
        Assert.IsFalse(run.IsPaused);
        await WaitUntilAsync(() => progress.Reports.Count > reportsWhilePaused, "progress after resume");
        run.Cancel();
        await run.Completion;
    }

    [TestMethod]
    public async Task CancelStopsTheEngine()
    {
        var progress = new RecordedProgress();
        using var run = StartSlowCreate(progress);
        await WaitForCalculationAsync(progress);

        run.Cancel();
        var result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), testContext.CancellationToken);

        Assert.AreEqual(EngineOutcome.Canceled, result.Outcome, result.Output);
        Assert.AreEqual(2, result.ExitCode, "The engine itself took the c key.");
    }

    [TestMethod]
    public async Task CancelWorksWhilePaused()
    {
        var progress = new RecordedProgress();
        using var run = StartSlowCreate(progress);
        await WaitForCalculationAsync(progress);
        run.Pause();
        await Task.Delay(500, testContext.CancellationToken);

        run.Cancel();
        var result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), testContext.CancellationToken);

        Assert.AreEqual(EngineOutcome.Canceled, result.Outcome, result.Output);
        Assert.AreEqual(2, result.ExitCode);
    }

    [TestMethod]
    public async Task CancelEndsAProgramThatIgnoresTheKey()
    {
        // ping never reads the console, like the engine in a phase without progress checks.
        var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        using var run = EngineRun.Start(
            new EngineCommand.List(RecoveryFile), _ => ["-n", "60", "127.0.0.1"], progress: null, ping, cancelGracePeriod: TimeSpan.FromSeconds(1));
        await Task.Delay(500, testContext.CancellationToken);

        run.Cancel();
        var result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), testContext.CancellationToken);

        Assert.AreEqual(EngineOutcome.Canceled, result.Outcome, result.Output);
    }

    private string RecoveryFile => Path.Combine(_folder, "fotos.par2");

    private Task<EngineResult> RunAsync(EngineCommand command, IProgress<EngineProgress>? progress = null)
    {
        var run = EngineRun.Start(command, progress, EngineProcess.ExePath);
        return run.Completion.WaitAsync(TimeSpan.FromMinutes(2), testContext.CancellationToken);
    }

    /// <summary>A create that takes several seconds: 100 MB, 100% redundancy, one CPU thread and no GPU.</summary>
    private EngineRun StartSlowCreate(IProgress<EngineProgress> progress)
    {
        var photo = WritePhoto("grande.jpg", 3, size: 100_000_000);
        var create = new EngineCommand.Create(RecoveryFile, _folder, [photo], RedundancyPercent: 100);
        return EngineRun.Start(
            create,
            runDirectory => [.. EngineArguments.For(create, runDirectory).Select(argument => argument == "/lc256" ? "/lc1" : argument)],
            progress,
            EngineProcess.ExePath,
            EngineRun.DefaultCancelGracePeriod);
    }

    private Task WaitForCalculationAsync(RecordedProgress progress) =>
        WaitUntilAsync(() => progress.Reports.Any(p => p.Activity == "Creating recovery slice" && p.Percent > 0), "the calculation to start");

    private async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.IsLessThan(deadline, DateTime.UtcNow, $"Timed out waiting for {what}.");
            await Task.Delay(50, testContext.CancellationToken);
        }
    }

    private string WritePhoto(string name, int seed, int size = 200_000)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
