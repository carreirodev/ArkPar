// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Screens;

namespace ArkPar.Tests.Screens;

/// <summary>
/// How each creation mode splits what was chosen into recovery sets, over the example on the screen:
/// "2019" holding "01 - Réveillon" and "07 - Viagem Bahia\Praia do Forte".
/// </summary>
[TestClass]
public sealed class CreationPlanTests
{
    private string _root = "";
    private string _year = "";

    [TestInitialize]
    public void CreateFolders()
    {
        _root = Path.Combine(Path.GetTempPath(), "ArkPar.Tests", Guid.NewGuid().ToString("N"));
        _year = Path.Combine(_root, "2019");
        Write(@"01 - Réveillon\IMG_0001.JPG", 1000);
        Write(@"01 - Réveillon\IMG_0002.JPG", 2000);
        Write(@"07 - Viagem Bahia\IMG_4469.JPG", 3000);
        Write(@"07 - Viagem Bahia\Praia do Forte\IMG_4501.JPG", 4000);
        Write(@"07 - Viagem Bahia\Praia do Forte\IMG_4502.HEIC", 5000);
    }

    [TestCleanup]
    public void DeleteFolders() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public void SingleSetMakesOneSetInTheChosenFolderCoveringEverything()
    {
        var plan = PlanFor(CreationMode.SingleSet);

        Assert.AreEqual(
            @"2019\2019.par2: 01 - Réveillon\IMG_0001.JPG, 01 - Réveillon\IMG_0002.JPG, 07 - Viagem Bahia\IMG_4469.JPG, 07 - Viagem Bahia\Praia do Forte\IMG_4501.JPG, 07 - Viagem Bahia\Praia do Forte\IMG_4502.HEIC",
            Describe(plan));
        Assert.AreEqual(15000, plan.Sets.Single().TotalSize);
    }

    [TestMethod]
    public void PerSubfolderMakesOneSetBesideEachSubfolderCoveringEverythingInIt()
    {
        var plan = PlanFor(CreationMode.PerSubfolder);

        Assert.AreEqual(
            """
            2019\01 - Réveillon.par2: 01 - Réveillon\IMG_0001.JPG, 01 - Réveillon\IMG_0002.JPG
            2019\07 - Viagem Bahia.par2: 07 - Viagem Bahia\IMG_4469.JPG, 07 - Viagem Bahia\Praia do Forte\IMG_4501.JPG, 07 - Viagem Bahia\Praia do Forte\IMG_4502.HEIC
            """,
            Describe(plan));
        Assert.AreEqual("2019, 2019", string.Join(", ", plan.Sets.Select(set => set.Where)), "Where each is saved.");
    }

    [TestMethod]
    public void PerSubfolderGivesTheFilesLooseInTheChosenFolderASetOfTheirOwn()
    {
        Write("capa.jpg", 100);

        var plan = PlanFor(CreationMode.PerSubfolder);

        Assert.AreEqual(
            """
            2019\01 - Réveillon.par2: 01 - Réveillon\IMG_0001.JPG, 01 - Réveillon\IMG_0002.JPG
            2019\07 - Viagem Bahia.par2: 07 - Viagem Bahia\IMG_4469.JPG, 07 - Viagem Bahia\Praia do Forte\IMG_4501.JPG, 07 - Viagem Bahia\Praia do Forte\IMG_4502.HEIC
            2019\2019.par2: capa.jpg
            """,
            Describe(plan));
    }

    [TestMethod]
    public void PerSubfolderRefusesASubfolderNamedAsTheLooseFilesSet()
    {
        // "2019\2019.par2" would be both the set of the files loose in 2019 and the set of the subfolder "2019".
        Write("capa.jpg", 100);
        Write(@"2019\IMG_0003.JPG", 100);

        var plan = CreationPlan.For(Selection.Read([_year]), CreationMode.PerSubfolder);

        Assert.AreEqual(
            "A subpasta \"2019\" e os arquivos soltos em \"2019\" teriam conjuntos com o mesmo nome, \"2019.par2\". Renomeie a subpasta ou escolha outro modo.",
            plan.Problem);
    }

    [TestMethod]
    public void PerSubfolderTakesASubfolderNamedAsTheChosenFolderWhenNoFileIsLoose()
    {
        Write(@"2019\IMG_0003.JPG", 100);

        var plan = PlanFor(CreationMode.PerSubfolder);

        Assert.AreEqual(@"2019\2019.par2: 2019\IMG_0003.JPG", Describe(plan).Split('\n')[^1]);
    }

    [TestMethod]
    public void PerFolderMakesOneSetInEachFolderWithFilesCoveringOnlyItsOwn()
    {
        var plan = PlanFor(CreationMode.PerFolder);

        Assert.AreEqual(
            """
            2019\01 - Réveillon\01 - Réveillon.par2: IMG_0001.JPG, IMG_0002.JPG
            2019\07 - Viagem Bahia\07 - Viagem Bahia.par2: IMG_4469.JPG
            2019\07 - Viagem Bahia\Praia do Forte\Praia do Forte.par2: IMG_4501.JPG, IMG_4502.HEIC
            """,
            Describe(plan));
        Assert.AreEqual("01 - Réveillon, 07 - Viagem Bahia, Praia do Forte", string.Join(", ", plan.Sets.Select(set => set.Name)));
    }

    [TestMethod]
    public void PerFileMakesOneSetBesideEachFileNamedWithItsExtension()
    {
        // A photo and its live video share a name: each keeps its own set.
        Write(@"07 - Viagem Bahia\IMG_4469.MOV", 100);

        var plan = PlanFor(CreationMode.PerFile);

        Assert.AreEqual(
            """
            2019\01 - Réveillon\IMG_0001.JPG.par2: IMG_0001.JPG
            2019\01 - Réveillon\IMG_0002.JPG.par2: IMG_0002.JPG
            2019\07 - Viagem Bahia\IMG_4469.JPG.par2: IMG_4469.JPG
            2019\07 - Viagem Bahia\IMG_4469.MOV.par2: IMG_4469.MOV
            2019\07 - Viagem Bahia\Praia do Forte\IMG_4501.JPG.par2: IMG_4501.JPG
            2019\07 - Viagem Bahia\Praia do Forte\IMG_4502.HEIC.par2: IMG_4502.HEIC
            """,
            Describe(plan));
        Assert.AreEqual(4000, plan.Sets[4].TotalSize);
    }

    [TestMethod]
    public void SeveralFoldersChosenSplitUnderTheFolderHoldingThem()
    {
        var selection = Selection.Read([Path.Combine(_year, "01 - Réveillon"), Path.Combine(_year, "07 - Viagem Bahia")]);

        var plan = CreationPlan.For(selection, CreationMode.PerSubfolder);

        Assert.AreEqual(
            """
            2019\01 - Réveillon.par2: 01 - Réveillon\IMG_0001.JPG, 01 - Réveillon\IMG_0002.JPG
            2019\07 - Viagem Bahia.par2: 07 - Viagem Bahia\IMG_4469.JPG, 07 - Viagem Bahia\Praia do Forte\IMG_4501.JPG, 07 - Viagem Bahia\Praia do Forte\IMG_4502.HEIC
            """,
            Describe(plan));
    }

    [TestMethod]
    public void TheFileLimitHoldsForEachSetNotForAllOfThem()
    {
        var selection = Selection.Read([_year]);

        Assert.IsNull(CreationPlan.For(selection, CreationMode.PerFolder, maxFiles: 2).Problem, "No folder has more than 2 files.");
        Assert.AreEqual(
            "Um conjunto protege até 2 arquivos, e foram escolhidos 5.",
            CreationPlan.For(selection, CreationMode.SingleSet, maxFiles: 2).Problem);
        Assert.AreEqual(
            "Um conjunto protege até 2 arquivos, e o conjunto em \"07 - Viagem Bahia\" teria 3.",
            CreationPlan.For(selection, CreationMode.PerSubfolder, maxFiles: 2).Problem);
    }

    [TestMethod]
    public void NothingToProtectMakesNoSets()
    {
        var plan = CreationPlan.For(Selection.Empty, CreationMode.PerFolder);

        Assert.IsEmpty(plan.Sets);
    }

    private CreationPlan PlanFor(CreationMode mode)
    {
        var selection = Selection.Read([_year]);
        Assert.IsNull(selection.Problem);
        var plan = CreationPlan.For(selection, mode);
        Assert.IsNull(plan.Problem);
        return plan;
    }

    /// <summary>Each set as "its index file: the files it covers", in the order the engine gets them, one per line.</summary>
    private string Describe(CreationPlan plan) => string.Join(
        "\n",
        plan.Sets.Select(set =>
        {
            Assert.AreEqual(set.BaseDirectory, Path.GetDirectoryName(set.RecoveryFile), "The index lies directly in the set's folder.");
            return Path.GetRelativePath(_root, set.RecoveryFile) + ": "
                + string.Join(", ", set.Files.Select(file => Path.GetRelativePath(set.BaseDirectory, file)));
        }));

    private void Write(string relativePath, int size)
    {
        var path = Path.Combine(_year, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
    }
}
