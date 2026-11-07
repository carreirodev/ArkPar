// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Screens;
using Microsoft.Win32;

namespace ArkPar.Tests.Screens;

/// <summary>
/// Opening .par2 files with ArkPar, over a throwaway key that stands for HKEY_CURRENT_USER, so the PC's real
/// associations are never touched.
/// </summary>
[TestClass]
public sealed class Par2AssociationTests
{
    private const string App = @"C:\Users\Ana\Downloads\ArkPar\ArkPar.exe";
    private const string UserChoice = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.par2\UserChoice";

    private string _keyPath = "";
    private RegistryKey _user = null!;

    [TestInitialize]
    public void CreateKey()
    {
        // A key of its own straight under Software, so deleting it leaves nothing behind.
        _keyPath = @"Software\ArkPar.Tests." + Guid.NewGuid().ToString("N");
        _user = Registry.CurrentUser.CreateSubKey(_keyPath);
    }

    [TestCleanup]
    public void DeleteKey()
    {
        _user.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_keyPath);
    }

    [TestMethod]
    public void NothingIsAssociatedBeforeTheUserAccepts()
    {
        Assert.IsFalse(new Par2Association(_user, App).IsRegistered);
        Assert.IsNull(_user.OpenSubKey(@"Software\Classes\.par2"));
    }

    [TestMethod]
    public void RegisteringOpensEachPar2WithThisCopyOfArkPar()
    {
        var association = new Par2Association(_user, App);

        association.Register();

        Assert.IsTrue(association.IsRegistered);
        Assert.AreEqual(Par2Association.ProgId, Value(@"Software\Classes\.par2", ""));
        Assert.AreEqual(@"""C:\Users\Ana\Downloads\ArkPar\ArkPar.exe"" ""%1""", Value(@"Software\Classes\ArkPar.par2\shell\open\command", ""));
        Assert.AreEqual("Arquivo de recuperação", Value(@"Software\Classes\ArkPar.par2", ""));
        Assert.AreEqual(@"""C:\Users\Ana\Downloads\ArkPar\ArkPar.exe"",0", Value(@"Software\Classes\ArkPar.par2\DefaultIcon", ""));

        // Listed under "Open with", too.
        using var openWith = _user.OpenSubKey(@"Software\Classes\.par2\OpenWithProgids");
        CollectionAssert.Contains(openWith!.GetValueNames(), Par2Association.ProgId);
    }

    [TestMethod]
    public void OpeningArkParFromAMovedFolderPointsTheDoubleClickThere()
    {
        new Par2Association(_user, App).Register();

        var moved = new Par2Association(_user, @"D:\Programas\ArkPar\ArkPar.exe");
        Assert.IsFalse(moved.IsRegistered, "Still pointing at the old folder.");

        moved.Register();

        Assert.IsTrue(moved.IsRegistered);
        Assert.AreEqual(@"""D:\Programas\ArkPar\ArkPar.exe"" ""%1""", Value(@"Software\Classes\ArkPar.par2\shell\open\command", ""));
    }

    [TestMethod]
    public void TurningItOffGivesPar2FilesBackToTheAppThatHadThem()
    {
        using (var par2 = _user.CreateSubKey(@"Software\Classes\.par2"))
        {
            par2.SetValue("", "MultiPar.par2");
        }

        // Registered twice (opened again later): the app from before ArkPar is still the one remembered.
        new Par2Association(_user, App).Register();
        new Par2Association(_user, App).Register();
        new Par2Association(_user, App).Unregister();

        Assert.AreEqual("MultiPar.par2", Value(@"Software\Classes\.par2", ""));
        Assert.IsNull(_user.OpenSubKey(@"Software\Classes\ArkPar.par2"));
        using var openWith = _user.OpenSubKey(@"Software\Classes\.par2\OpenWithProgids");
        CollectionAssert.DoesNotContain(openWith?.GetValueNames() ?? [], Par2Association.ProgId);
    }

    [TestMethod]
    public void TurningItOffLeavesNothingBehindWhenNoOtherAppHadPar2Files()
    {
        var association = new Par2Association(_user, App);
        association.Register();

        association.Unregister();

        Assert.IsFalse(association.IsRegistered);
        Assert.IsNull(_user.OpenSubKey(@"Software\Classes\.par2"));
        Assert.IsNull(_user.OpenSubKey(@"Software\Classes\ArkPar.par2"));
    }

    [TestMethod]
    public void TurningItOffWhenItWasNeverOnChangesNothing()
    {
        new Par2Association(_user, App).Unregister();

        Assert.IsNull(_user.OpenSubKey(@"Software\Classes\.par2"));
    }

    [TestMethod]
    [DataRow(null, false)]
    [DataRow("ArkPar.par2", false)]
    [DataRow("MultiPar.par2", true)]
    public void TellsWhenWindowsWasToldToAlwaysOpenPar2FilesWithAnotherApp(string? chosen, bool otherApp)
    {
        if (chosen is not null)
        {
            using var userChoice = _user.CreateSubKey(UserChoice);
            userChoice.SetValue("ProgId", chosen);
        }

        var association = new Par2Association(_user, App);
        association.Register();

        Assert.AreEqual(otherApp, association.AnotherAppChosen);
    }

    private object? Value(string key, string name)
    {
        using var opened = _user.OpenSubKey(key);
        return opened?.GetValue(name);
    }
}
