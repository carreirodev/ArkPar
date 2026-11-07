// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Screens;

namespace ArkPar.Tests.Screens;

/// <summary>What a set covers, from the files and folders dropped on the Create screen, over a real temporary folder.</summary>
[TestClass]
public sealed class SelectionTests
{
    private string _root = "";
    private string _trip = "";

    [TestInitialize]
    public void CreateFolders()
    {
        _root = Path.Combine(Path.GetTempPath(), "ArkPar.Tests", Guid.NewGuid().ToString("N"));
        _trip = Path.Combine(_root, "07 - Viagem Bahia");
        Directory.CreateDirectory(Path.Combine(_trip, "Praia do Forte"));
        Directory.CreateDirectory(Path.Combine(_trip, "Vídeos"));
    }

    [TestCleanup]
    public void DeleteFolders() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public void OneFolderHoldsItsSetAndNamesIt()
    {
        Write(@"IMG_4469.JPG", 3000);
        Write(@"Praia do Forte\IMG_4501.JPG", 2048);
        Write(@"Praia do Forte\IMG_4502.HEIC", 1024);
        Write(@"Vídeos\VID_0033.MP4", 5000);

        var selection = Selection.Read([_trip]);

        Assert.IsNull(selection.Problem);
        Assert.AreEqual(_trip, selection.BaseDirectory);
        Assert.AreEqual(Path.Combine(_trip, "07 - Viagem Bahia.par2"), selection.RecoveryFile);
        Assert.HasCount(4, selection.Files);
        Assert.AreEqual(11072, selection.TotalSize);
        Assert.AreEqual("4 arquivos · 11 KB", selection.Summary);

        var folder = selection.Tree.Single();
        Assert.AreEqual(("07 - Viagem Bahia", true, "4 · 11 KB"), (folder.Name, folder.IsFolder, folder.Meta));
        Assert.AreEqual(
            "Praia do Forte, Vídeos, IMG_4469.JPG",
            string.Join(", ", folder.Children.Select(child => child.Name)),
            "Folders first, then files, each by name.");
        Assert.AreEqual("2 · 3,0 KB", folder.Children[0].Meta);
        Assert.AreEqual("2,9 KB", folder.Children[2].Meta);
    }

    [TestMethod]
    public void HiddenSystemAndRecoveryFilesStayOut()
    {
        var photo = Write("IMG_4469.JPG", 100);
        File.SetAttributes(Write("Thumbs.db", 100), FileAttributes.Hidden | FileAttributes.System);
        File.SetAttributes(Write("rascunho.jpg", 100), FileAttributes.Hidden);
        Write("07 - Viagem Bahia.par2", 100);
        Write(@"Praia do Forte\Praia do Forte.vol0+3.par2", 100);

        var selection = Selection.Read([_trip]);

        CollectionAssert.AreEqual(new[] { photo }, selection.Files.ToArray());
        Assert.AreEqual("IMG_4469.JPG", string.Join(", ", selection.Tree.Single().Children.Select(child => child.Name)), "A folder with nothing to protect is not shown.");
    }

    [TestMethod]
    public void SeveralItemsLiveInTheFolderThatHoldsThem()
    {
        var photo = Write("IMG_4469.JPG", 100);
        Write(@"Praia do Forte\IMG_4501.JPG", 100);

        var selection = Selection.Read([photo, Path.Combine(_trip, "Praia do Forte")]);

        Assert.AreEqual(_trip, selection.BaseDirectory);
        Assert.AreEqual(Path.Combine(_trip, "07 - Viagem Bahia.par2"), selection.RecoveryFile);
        Assert.AreEqual("Praia do Forte, IMG_4469.JPG", string.Join(", ", selection.Tree.Select(node => node.Name)));
        Assert.HasCount(2, selection.Files);
    }

    [TestMethod]
    public void OneFileNamesTheSetAfterItself()
    {
        var video = Write(@"Vídeos\VID_0033.MP4", 100);

        var selection = Selection.Read([video]);

        Assert.AreEqual(Path.Combine(_trip, "Vídeos"), selection.BaseDirectory);
        Assert.AreEqual(Path.Combine(_trip, "Vídeos", "VID_0033.par2"), selection.RecoveryFile);
    }

    [TestMethod]
    public void WhatIsInsideAChosenFolderIsNotCountedTwice()
    {
        var photo = Write(@"Praia do Forte\IMG_4501.JPG", 100);

        var selection = Selection.Read([photo, _trip, _trip + @"\", Path.Combine(_trip, "Praia do Forte")]);

        CollectionAssert.AreEqual(new[] { _trip }, selection.Roots.ToArray());
        Assert.HasCount(1, selection.Files);
    }

    [TestMethod]
    public void ItemsOnDifferentDrivesCannotShareASet()
    {
        Write("IMG_4469.JPG", 100);
        var otherDrive = Path.GetPathRoot(_root)!.StartsWith('Z') ? @"Y:\Fotos" : @"Z:\Fotos";

        var selection = Selection.Read([_trip, otherDrive]);

        Assert.AreEqual("Os arquivos de um conjunto precisam estar no mesmo disco.", selection.Problem);
        Assert.IsNull(selection.RecoveryFile);
    }

    [TestMethod]
    [DataRow(new[] { @"D:\Fotos", @"D:\Vídeos" }, @"D:\")]
    [DataRow(new[] { @"D:\IMG_0001.JPG" }, @"D:\")]
    [DataRow(new[] { @"D:\Fotos\2019\a.jpg", @"D:\Fotos\2020" }, @"D:\Fotos")]
    public void ItemsAtTheTopOfADriveShareItsRoot(string[] roots, string expected)
    {
        // Disks of their own cannot be made in a test, so the path rule is checked on its own.
        Assert.AreEqual(expected, Selection.CommonParent([.. roots]));
        Assert.IsTrue(roots.All(root => Selection.IsInside(root, expected)));
    }

    [TestMethod]
    public void AFolderLinkIsNotFollowed()
    {
        Write(@"Praia do Forte\IMG_4501.JPG", 100);

        // A link back up the tree would loop.
        Directory.CreateSymbolicLink(Path.Combine(_trip, "atalho"), _trip);

        var selection = Selection.Read([_trip]);

        Assert.HasCount(1, selection.Files);
        Assert.AreEqual("Praia do Forte", string.Join(", ", selection.Tree.Single().Children.Select(child => child.Name)));
    }

    [TestMethod]
    public void AFolderWithNothingToProtectCannotMakeASet()
    {
        File.SetAttributes(Write("desktop.ini", 100), FileAttributes.Hidden | FileAttributes.System);

        var selection = Selection.Read([_trip]);

        Assert.AreEqual("Não há arquivos para proteger no que foi escolhido.", selection.Problem);
        Assert.AreEqual("0 arquivos · 0 KB", selection.Summary);
    }

    [TestMethod]
    public void FindsTheRecoveryFilesOfASetAlreadyMadeHere()
    {
        Write("IMG_4469.JPG", 100);
        var index = Write("07 - Viagem Bahia.par2", 100);
        var recoveryFile = Write("07 - Viagem Bahia.vol0+19.par2", 100);
        Write("07 - Viagem Bahia 2.par2", 100);
        Write("07 - Viagem Bahia.vol2.par2", 100);

        var selection = Selection.Read([_trip]);

        CollectionAssert.AreEqual(new[] { index, recoveryFile }, selection.ExistingRecoveryFiles().ToArray(), "Not the sets named \"07 - Viagem Bahia 2\" or \"07 - Viagem Bahia.vol2\".");
    }

    private string Write(string name, int size)
    {
        var path = Path.Combine(_trip, name);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }
}
