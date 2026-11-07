// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Screens;

namespace ArkPar.Tests.Screens;

/// <summary>The settings the Settings screen keeps between sessions, in a real temporary folder.</summary>
[TestClass]
public sealed class SettingsFileTests
{
    private string _root = "";
    private string _path = "";

    [TestInitialize]
    public void CreateFolder()
    {
        _root = Path.Combine(Path.GetTempPath(), "ArkPar.Tests", Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_root, "ArkPar", "settings.json");
    }

    [TestCleanup]
    public void DeleteFolder()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void FirstRunUsesTheGpuAllCoresAndHasNotAskedAboutPar2FilesYet()
    {
        var settings = new SettingsFile(_path).Load();

        Assert.IsTrue(settings.UseGpu);
        Assert.IsNull(settings.Threads);
        Assert.IsNull(settings.OpenPar2Files);
        Assert.AreEqual(new ArkPar.Engine.EngineProcessing(UseGpu: true, Threads: null), settings.Processing);
    }

    [TestMethod]
    public void WhatIsSavedIsWhatTheNextSessionLoads()
    {
        var saved = new AppSettings(UseGpu: false, Threads: 4, OpenPar2Files: true);

        new SettingsFile(_path).Save(saved);

        Assert.AreEqual(saved, new SettingsFile(_path).Load());
        Assert.DoesNotContain("Processing", File.ReadAllText(_path), "Only what the user chose, not what follows from it.");
    }

    [TestMethod]
    public void SavingAgainReplacesTheEarlierChoice()
    {
        var file = new SettingsFile(_path);
        file.Save(new AppSettings(OpenPar2Files: false));

        file.Save(new AppSettings(Threads: 2, OpenPar2Files: true));

        Assert.AreEqual(new AppSettings(Threads: 2, OpenPar2Files: true), file.Load());
    }

    [TestMethod]
    [DataRow("{ \"UseGpu\": tru")]
    [DataRow("")]
    [DataRow("{ \"Threads\": 64 }")]
    [DataRow("{ \"Threads\": 0 }")]
    public void AFileThatCannotBeReadIsReportedNotTakenForTheDefaults(string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, contents);

        var failure = Assert.ThrowsExactly<InvalidDataException>(() => new SettingsFile(_path).Load());

        Assert.Contains(_path, failure.Message);
    }
}
