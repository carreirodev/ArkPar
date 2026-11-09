// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using ArkPar.Screens;

namespace ArkPar.Tests.Screens;

/// <summary>The recovery sets found inside a folder chosen on the Verify and repair screen, over a real temporary folder.</summary>
[TestClass]
public sealed class SetSearchTests
{
    private static readonly string[] PraiaAndItsReadyDraft = ["Praia", "Praia.arkpar-pronto"];
    private static readonly string[] HiddenAndSystemSets = ["Natal", "Oculto", "Praia", "Privado"];

    private string _root = "";
    private string _year = "";

    [TestInitialize]
    public void CreateFolders()
    {
        _root = Path.Combine(Path.GetTempPath(), "ArkPar.Tests", Guid.NewGuid().ToString("N"));
        _year = Path.Combine(_root, "2019");
        Directory.CreateDirectory(_year);
    }

    [TestCleanup]
    public void DeleteFolders()
    {
        // Hidden folders and read-only files would otherwise block the delete.
        foreach (var entry in new DirectoryInfo(_root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            entry.Attributes = entry is DirectoryInfo ? FileAttributes.Directory : FileAttributes.Normal;
        }

        Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void FindsEverySetAtAnyDepthByFolderThenName()
    {
        Write(@"Praia\Praia.par2");
        Write(@"Praia\Praia.vol0+10.par2");
        Write(@"Praia\IMG_0001.JPG");
        Write(@"Natal\Ceia\Ceia.par2");
        Write(@"2019.par2");
        Write(@"Natal\b.par2");
        Write(@"Natal\a.par2");

        var found = SetSearch.Find(_year);

        CollectionAssert.AreEqual(
            new[]
            {
                new FoundSet(Path.Combine(_year, "2019.par2"), _year, "2019"),
                new FoundSet(Path.Combine(_year, @"Natal\a.par2"), Path.Combine(_year, "Natal"), "a"),
                new FoundSet(Path.Combine(_year, @"Natal\b.par2"), Path.Combine(_year, "Natal"), "b"),
                new FoundSet(Path.Combine(_year, @"Natal\Ceia\Ceia.par2"), Path.Combine(_year, @"Natal\Ceia"), "Ceia"),
                new FoundSet(Path.Combine(_year, @"Praia\Praia.par2"), Path.Combine(_year, "Praia"), "Praia"),
            },
            found.Sets.ToArray());
        Assert.IsEmpty(found.LeftOut);
    }

    [TestMethod]
    public void SetBesideTheSubfolderOfItsNameProtectsThatSubfolder()
    {
        // Made per subfolder: "Praia.par2" lies in 2019, beside the "Praia" it protects. "2019.par2" covers the loose files.
        Write("Praia.par2");
        Write(@"Praia\IMG_0001.JPG");
        Write("2019.par2");
        Write(@"Natal\Natal.par2");

        var found = SetSearch.Find(_year);

        Assert.AreEqual(
            @"2019.par2 → 2019; Praia.par2 → 2019\Praia; Natal\Natal.par2 → 2019\Natal",
            string.Join("; ", found.Sets.Select(set => $"{Path.GetRelativePath(_year, set.RecoveryFile)} → {Path.GetRelativePath(_root, set.ProtectedFolder)}")));
        Assert.IsTrue(found.Sets.All(set => set.Folder == Path.GetDirectoryName(set.RecoveryFile)), "Where each lies.");
    }

    [TestMethod]
    public void SetNamedAsItsOwnFolderProtectsThatFolderEvenBesideASubfolderOfTheName()
    {
        // "2019\2019.par2" is the set of 2019 itself (one set for everything, or its loose files), not of "2019\2019".
        Write("2019.par2");
        Write(@"2019\2019.par2");

        var found = SetSearch.Find(_year);

        Assert.AreEqual(@"2019; 2019\2019", string.Join("; ", found.Sets.Select(set => Path.GetRelativePath(_root, set.ProtectedFolder))));
    }

    [TestMethod]
    public void ReadyDraftBesideASubfolderProtectsItAsTheSetItWillBe()
    {
        Write("Praia.arkpar-pronto.par2");
        Write(@"Praia\IMG_0001.JPG");
        Write("praia.vol0+1.par2");

        var found = SetSearch.Find(_year);

        Assert.AreEqual(@"2019\Praia; 2019\Praia", string.Join("; ", found.Sets.Select(set => Path.GetRelativePath(_root, set.ProtectedFolder))), "As the folder is spelled.");
    }

    [TestMethod]
    public void SetWithoutItsIndexIsVerifiedFromItsFirstRecoveryFile()
    {
        Write(@"Praia\Praia.vol10+10.par2");
        Write(@"Praia\Praia.vol0+10.par2");

        var found = SetSearch.Find(_year);

        Assert.AreEqual(Path.Combine(_year, @"Praia\Praia.vol0+10.par2"), found.Sets.Single().RecoveryFile);
        Assert.AreEqual("Praia", found.Sets.Single().Name);
    }

    [TestMethod]
    public void HiddenAndSystemFoldersAndHiddenSetsAreSearchedToo()
    {
        Write(@"Praia\Praia.par2");
        File.SetAttributes(Write(@"Praia\Oculto.par2"), FileAttributes.Hidden);
        Write(@"Privado\Privado.par2");
        File.SetAttributes(Path.Combine(_year, "Privado"), FileAttributes.Directory | FileAttributes.Hidden);
        Write(@"Natal\Natal.par2");
        File.SetAttributes(Path.Combine(_year, "Natal"), FileAttributes.Directory | FileAttributes.System | FileAttributes.ReadOnly);

        var found = SetSearch.Find(_year);

        CollectionAssert.AreEqual(HiddenAndSystemSets, found.Sets.Select(set => set.Name).ToArray());
        Assert.IsEmpty(found.LeftOut);
    }

    [TestMethod]
    public void SetsKeptAsideAndTheRecycleBinStayOut()
    {
        Write(@"Praia\Praia.par2");
        Write(@"Praia\Praia.par2.anterior");
        Write(@"$RECYCLE.BIN\S-1-5-21\$RABC123.par2");
        Write(@"System Volume Information\Algo.par2");

        var found = SetSearch.Find(_year);

        Assert.AreEqual("Praia", found.Sets.Single().Name);
        Assert.IsEmpty(found.LeftOut, "Windows' own folders hold no set of the user's.");
    }

    [TestMethod]
    public void LinkToAnotherFolderIsLeftOutAndSaidSo()
    {
        Write(@"Praia\Praia.par2");
        var elsewhere = Path.Combine(_root, "Outro");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllBytes(Path.Combine(elsewhere, "Outro.par2"), [1, 2, 3]);
        var link = Path.Combine(_year, "Atalho");
        MakeJunction(link, elsewhere);
        try
        {
            var found = SetSearch.Find(_year);

            Assert.AreEqual("Praia", found.Sets.Single().Name);
            CollectionAssert.AreEqual(new[] { new LeftOutFolder(link, LeftOutReason.Link) }, found.LeftOut.ToArray());
        }
        finally
        {
            // Removes the link alone: the clean-up would otherwise trip on it.
            Directory.Delete(link);
        }
    }

    [TestMethod]
    public void FolderThatCannotBeReadIsLeftOutAndTheOthersAreSearched()
    {
        Write(@"Praia\Praia.par2");
        Write(@"Trancada\Trancada.par2");
        var locked = new DirectoryInfo(Path.Combine(_year, "Trancada"));
        // The test runs as a Windows user, so there is one.
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = locked.GetAccessControl();
        security.AddAccessRule(deny);
        locked.SetAccessControl(security);
        try
        {
            var found = SetSearch.Find(_year);

            Assert.AreEqual("Praia", found.Sets.Single().Name);
            CollectionAssert.AreEqual(new[] { new LeftOutFolder(locked.FullName, LeftOutReason.Unreadable) }, found.LeftOut.ToArray());
        }
        finally
        {
            security.RemoveAccessRule(deny);
            locked.SetAccessControl(security);
        }
    }

    [TestMethod]
    public void DraftLeftByAnInterruptedCreateIsNotASet()
    {
        Write(@"Praia\Praia.par2");
        Write(@"Praia\Praia.arkpar-novo.par2");
        Write(@"Praia\Praia.arkpar-novo.vol0+10.par2");
        Write(@"Natal\Natal.ARKPAR-NOVO.vol0+10.par2");

        var found = SetSearch.Find(_year);

        Assert.AreEqual("Praia", found.Sets.Single().Name, "A draft is never whole: verifying it would tell a problem where the files are protected.");
        Assert.IsEmpty(found.LeftOut);
    }

    [TestMethod]
    public void ReadyDraftIsFoundUnderItsOwnName()
    {
        Write(@"Praia\Praia.par2");
        Write(@"Praia\Praia.arkpar-pronto.par2");

        var found = SetSearch.Find(_year);

        CollectionAssert.AreEqual(PraiaAndItsReadyDraft, found.Sets.Select(set => set.Name).ToArray(), "It is whole, and may be the only set of its files.");
    }

    [TestMethod]
    public void FolderWithoutSetsFindsNone()
    {
        Write(@"Praia\IMG_0001.JPG");

        Assert.IsEmpty(SetSearch.Find(_year).Sets);
    }

    private string Write(string relativePath)
    {
        var path = Path.Combine(_year, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _year);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private static void MakeJunction(string link, string target)
    {
        // A junction needs no administrator rights, unlike a symbolic link.
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { CreateNoWindow = true, RedirectStandardOutput = true }) ?? throw new InvalidOperationException("cmd did not start.");
        mklink.WaitForExit();
        Assert.AreEqual(0, mklink.ExitCode, "mklink /J failed.");
    }
}
