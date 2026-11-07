// SPDX-License-Identifier: GPL-2.0-or-later

namespace ArkPar.Tests.Engine;

/// <summary>
/// Proves that the engine built from source/par2j works: it creates a recovery set,
/// verifies it as complete, and notices when a protected file is damaged.
/// </summary>
[TestClass]
public sealed class EngineSmokeTests(TestContext testContext)
{
    private string _workDir = "";

    [TestInitialize]
    public void CreateWorkDir()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ArkPar.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    [TestCleanup]
    public void DeleteWorkDir()
    {
        Directory.Delete(_workDir, recursive: true);
    }

    [TestMethod]
    public async Task CreatedRecoverySetVerifiesAsComplete()
    {
        var photo = WritePhoto();
        var recoveryFile = await CreateRecoverySetAsync(photo);

        var verify = await RunEngineAsync("v", recoveryFile);
        Assert.AreEqual(0, verify.ExitCode, verify.Output);
        Assert.Contains("All Files Complete", verify.Output);
    }

    [TestMethod]
    public async Task VerifyReportsDamageAfterOneByteOfThePhotoChanges()
    {
        var photo = WritePhoto();
        var recoveryFile = await CreateRecoverySetAsync(photo);

        var bytes = File.ReadAllBytes(photo);
        bytes[100] ^= 0xFF;
        File.WriteAllBytes(photo, bytes);

        var verify = await RunEngineAsync("v", recoveryFile);
        Assert.AreEqual(0, verify.ExitCode & EngineProcess.FatalError, verify.Output);
        Assert.AreNotEqual(0, verify.ExitCode & EngineProcess.InputFilesIncomplete, verify.Output);
    }

    private string WritePhoto()
    {
        // Fixed seed: the same "photo" every run.
        var bytes = new byte[200_000];
        new Random(1).NextBytes(bytes);
        var path = Path.Combine(_workDir, "photo.jpg");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Creates a recovery set with 10% redundancy and returns its index recovery file.</summary>
    private async Task<string> CreateRecoverySetAsync(string sourceFile)
    {
        var recoveryFile = Path.ChangeExtension(sourceFile, ".par2");
        var create = await RunEngineAsync("c", "/rr10", recoveryFile, sourceFile);
        Assert.AreEqual(0, create.ExitCode, create.Output);
        Assert.IsTrue(File.Exists(recoveryFile), create.Output);
        return recoveryFile;
    }

    private Task<EngineProcess.Result> RunEngineAsync(params string[] arguments) =>
        EngineProcess.RunAsync(testContext.CancellationToken, arguments);
}
