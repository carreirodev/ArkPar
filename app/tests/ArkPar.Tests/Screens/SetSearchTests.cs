// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Screens;

namespace ArkPar.Tests.Screens;

/// <summary>The recovery sets found inside a folder chosen on the Verify and repair screen, over a real temporary folder.</summary>
[TestClass]
public sealed class SetSearchTests
{
    private static readonly string[] PraiaAndItsDraft = ["Praia", "Praia.arkpar-novo"];

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
        Assert.IsEmpty(found.Unreadable);
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
    public void HiddenFoldersAndFilesAndSetsKeptAsideStayOut()
    {
        Write(@"Praia\Praia.par2");
        Write(@"Praia\Praia.par2.anterior");
        File.SetAttributes(Write(@"Praia\Oculto.par2"), FileAttributes.Hidden);
        Write(@"Lixo\Lixo.par2");
        File.SetAttributes(Path.Combine(_year, "Lixo"), FileAttributes.Directory | FileAttributes.Hidden);

        var found = SetSearch.Find(_year);

        Assert.AreEqual("Praia", found.Sets.Single().Name);
    }

    [TestMethod]
    public void DraftLeftByAnEarlierCreateIsFoundUnderItsOwnName()
    {
        Write(@"Praia\Praia.par2");
        Write(@"Praia\Praia.arkpar-novo.par2");

        var found = SetSearch.Find(_year);

        CollectionAssert.AreEqual(PraiaAndItsDraft, found.Sets.Select(set => set.Name).ToArray());
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
}
