// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;
using ArkPar.Screens;
using Microsoft.Win32;

namespace ArkPar.Tests.Screens;

/// <summary>
/// The Settings screen, driven as the user would, over a real temporary settings file and a throwaway registry key
/// standing for HKEY_CURRENT_USER, on a PC with 8 processor threads.
/// </summary>
[TestClass]
public sealed class SettingsScreenTests
{
    private const string App = @"C:\Users\Ana\Downloads\ArkPar\ArkPar.exe";
    private static readonly string[] EightThreads = ["Automático", "1 núcleo", "2 núcleos", "3 núcleos", "4 núcleos", "5 núcleos", "6 núcleos", "7 núcleos", "8 núcleos"];

    private string _root = "";
    private SettingsFile _file = null!;
    private string _keyPath = "";
    private RegistryKey _user = null!;
    private int _explorerTold;

    [TestInitialize]
    public void Create()
    {
        _root = Path.Combine(Path.GetTempPath(), "ArkPar.Tests", Guid.NewGuid().ToString("N"));
        _file = new SettingsFile(Path.Combine(_root, "ArkPar", "settings.json"));
        _keyPath = @"Software\ArkPar.Tests." + Guid.NewGuid().ToString("N");
        _user = Registry.CurrentUser.CreateSubKey(_keyPath);
    }

    [TestCleanup]
    public void Delete()
    {
        _user.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_keyPath);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void FirstRunUsesTheGpuAndEveryCoreAndStillHasToAskAboutPar2Files()
    {
        var screen = NewScreen();

        Assert.IsTrue(screen.UseGpu);
        Assert.AreEqual("Automático", screen.Threads.Text);
        CollectionAssert.AreEqual(EightThreads, screen.ThreadChoices.Select(choice => choice.Text).ToArray());
        Assert.AreEqual(EngineProcessing.Default, screen.Processing);
        Assert.IsTrue(screen.MustAskAboutPar2Files);
        Assert.IsFalse(screen.OpenPar2Files);
        Assert.IsFalse(screen.HasBanner);
    }

    [TestMethod]
    public void NeverOffersMoreThreadsThanTheEngineTakes()
    {
        var screen = NewScreen(processorCount: 64);

        Assert.AreEqual("32 núcleos", screen.ThreadChoices[^1].Text);
    }

    [TestMethod]
    public void GpuAndCoresAreKeptForTheNextSessionAndTheNextTask()
    {
        var screen = NewScreen();

        screen.UseGpu = false;
        screen.Threads = screen.ThreadChoices.Single(choice => choice.Value == 4);

        Assert.AreEqual(new EngineProcessing(UseGpu: false, Threads: 4), screen.Processing);
        var next = NewScreen();
        Assert.IsFalse(next.UseGpu);
        Assert.AreEqual("4 núcleos", next.Threads.Text);
    }

    [TestMethod]
    public void AcceptingTheFirstRunQuestionOpensPar2FilesWithArkPar()
    {
        var screen = NewScreen();

        screen.AnswerPar2Question(open: true);

        Assert.IsTrue(screen.OpenPar2Files);
        Assert.IsFalse(screen.MustAskAboutPar2Files);
        Assert.IsTrue(new Par2Association(_user, App).IsRegistered);
        Assert.AreEqual(1, _explorerTold, "Explorer picks the change up at once.");
        Assert.IsFalse(NewScreen().MustAskAboutPar2Files, "Asked once only.");
    }

    [TestMethod]
    public void DecliningTheFirstRunQuestionChangesNothingAndIsNotAskedAgain()
    {
        var screen = NewScreen();

        screen.AnswerPar2Question(open: false);

        Assert.IsFalse(screen.OpenPar2Files);
        Assert.IsFalse(new Par2Association(_user, App).IsRegistered);
        Assert.IsNull(_user.OpenSubKey(@"Software\Classes\.par2"));
        Assert.AreEqual(0, _explorerTold);
        Assert.IsFalse(NewScreen().MustAskAboutPar2Files);
    }

    [TestMethod]
    public void TheSwitchInSettingsTurnsTheAssociationOnAndOff()
    {
        var screen = NewScreen();
        screen.AnswerPar2Question(open: false);

        screen.OpenPar2Files = true;
        Assert.IsTrue(new Par2Association(_user, App).IsRegistered);

        screen.OpenPar2Files = false;
        Assert.IsFalse(new Par2Association(_user, App).IsRegistered);
        Assert.IsNull(_user.OpenSubKey(@"Software\Classes\.par2"), "Nothing left behind.");
        Assert.IsFalse(NewScreen().OpenPar2Files);
        Assert.AreEqual(2, _explorerTold);
    }

    [TestMethod]
    public void OpeningArkParFromAMovedFolderPointsTheDoubleClickThere()
    {
        NewScreen().AnswerPar2Question(open: true);
        const string moved = @"D:\Programas\ArkPar\ArkPar.exe";

        var screen = new SettingsScreen(_file, new Par2Association(_user, moved), processorCount: 8, () => _explorerTold++);
        screen.KeepPar2FilesOpeningHere();

        Assert.IsTrue(new Par2Association(_user, moved).IsRegistered);
        Assert.AreEqual(2, _explorerTold);
    }

    [TestMethod]
    public void OpeningArkParAgainFromTheSameFolderTouchesNothing()
    {
        NewScreen().AnswerPar2Question(open: true);

        NewScreen().KeepPar2FilesOpeningHere();

        Assert.AreEqual(1, _explorerTold);
    }

    [TestMethod]
    public void ADeclinedAssociationIsNotMadeOnLaunch()
    {
        NewScreen().AnswerPar2Question(open: false);

        NewScreen().KeepPar2FilesOpeningHere();

        Assert.IsNull(_user.OpenSubKey(@"Software\Classes\.par2"));
    }

    [TestMethod]
    public void SaysWhenWindowsStillOpensPar2FilesWithAnotherApp()
    {
        using (var userChoice = _user.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.par2\UserChoice"))
        {
            userChoice.SetValue("ProgId", "MultiPar.par2");
        }

        var screen = NewScreen();
        Assert.IsFalse(screen.AnotherAppChosen, "Only worth saying once the user wants ArkPar to open them.");

        screen.AnswerPar2Question(open: true);

        Assert.IsTrue(screen.AnotherAppChosen);
    }

    [TestMethod]
    public void SettingsThatCannotBeReadAreSaidToBeBackToTheDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file.Path)!);
        File.WriteAllText(_file.Path, "{ \"UseGpu\": tru");

        var screen = NewScreen();

        Assert.IsTrue(screen.UseGpu);
        Assert.AreEqual(new Banner(BannerKind.Warning, "As configurações salvas não puderam ser lidas e voltaram ao padrão.", Detail: null), screen.Banner);
        Assert.IsNotNull(screen.Details);
    }

    [TestMethod]
    public void AChangeThatCannotBeSavedSaysSoAndIsUndone()
    {
        var screen = NewScreen();

        // A folder where the file goes: the save fails.
        Directory.CreateDirectory(_file.Path);
        screen.UseGpu = false;

        Assert.IsTrue(screen.UseGpu);
        Assert.AreEqual(BannerKind.Error, screen.Banner?.Kind);
        Assert.AreEqual("Não foi possível salvar as configurações.", screen.Banner?.Title);
        Assert.AreEqual(EngineProcessing.Default, screen.Processing);
    }

    [TestMethod]
    public void AcceptingWhenTheAnswerCannotBeSavedLeavesPar2FilesAsTheyWere()
    {
        var screen = NewScreen();
        Directory.CreateDirectory(_file.Path);

        screen.AnswerPar2Question(open: true);

        Assert.IsFalse(screen.OpenPar2Files);
        Assert.IsFalse(new Par2Association(_user, App).IsRegistered, "The switch and Windows agree.");
        Assert.AreEqual("Não foi possível salvar as configurações.", screen.Banner?.Title);
        Assert.AreEqual(0, _explorerTold);
    }

    [TestMethod]
    public void TurningOffWhenTheChoiceCannotBeSavedKeepsPar2FilesOpeningHere()
    {
        var screen = NewScreen();
        screen.AnswerPar2Question(open: true);
        File.Delete(_file.Path);
        Directory.CreateDirectory(_file.Path);

        screen.OpenPar2Files = false;

        Assert.IsTrue(screen.OpenPar2Files);
        Assert.IsTrue(new Par2Association(_user, App).IsRegistered, "The switch and Windows agree.");
        Assert.AreEqual("Não foi possível salvar as configurações.", screen.Banner?.Title);
    }

    [TestMethod]
    public void AChangeInAnotherWindowIsUsedHereAndNotUndoneByAChangeHere()
    {
        // A double click on a .par2 while ArkPar is open opens a second window, in a process of its own.
        var first = NewScreen();
        var second = NewScreen();

        first.UseGpu = false;
        Assert.AreEqual(new EngineProcessing(UseGpu: false, Threads: null), second.Processing, "The second window's next task.");

        second.Threads = second.ThreadChoices.Single(choice => choice.Value == 4);

        Assert.AreEqual(new AppSettings(UseGpu: false, Threads: 4), _file.Load());
        first.Refresh();
        Assert.AreEqual("4 núcleos", first.Threads.Text);
    }

    private SettingsScreen NewScreen(int processorCount = 8) =>
        new(_file, new Par2Association(_user, App), processorCount, () => _explorerTold++);
}
