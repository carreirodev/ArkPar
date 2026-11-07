// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;
using ArkPar.Screens;

namespace ArkPar.Tests.Screens;

/// <summary>
/// Each reason the list gives for a set, over a real temporary folder "2026": "Novembro" with three photos,
/// its set "Novembro.par2" made on 1 November, and "Novembro\Feriado" with one more photo.
/// </summary>
[TestClass]
public sealed class SetComparisonTests : IDisposable
{
    private static readonly DateTime SetMadeAt = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TakenAt = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ArkPar.Tests", Guid.NewGuid().ToString("N"), "2026");
    private readonly string _november;
    private readonly string[] _photos;
    private readonly TestContext _testContext;

    public SetComparisonTests(TestContext testContext)
    {
        _testContext = testContext;
        _november = Path.Combine(_root, "Novembro");
        Directory.CreateDirectory(Path.Combine(_november, "Feriado"));
        _photos = [WritePhoto("IMG_0001.JPG", 1000), WritePhoto("IMG_0002.JPG", 2000), WritePhoto("IMG_0003.JPG", 3000)];
    }

    private string Index => Path.Combine(_november, "Novembro.par2");

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);

    [TestMethod]
    public async Task WithoutASetTheSetIsCreated()
    {
        var item = await ReviewAsync(Planned(_photos), listed: null);

        Assert.AreEqual("Criar · sem conjunto", item.StatusText);
        Assert.IsTrue(item.IsIncluded);
        Assert.IsNull(item.Caution);
    }

    [TestMethod]
    public async Task ASetCoveringTheVeryFilesUnchangedIsUpToDateAndSkipped()
    {
        var item = await ReviewAsync(Planned(_photos), Listed(_photos));

        Assert.AreEqual("Pular · em dia", item.StatusText);
        Assert.IsFalse(item.IsIncluded);
        Assert.IsTrue(item.Comparison.Replaces);
    }

    [TestMethod]
    public async Task FilesAddedSinceTheSetAreNew()
    {
        var added = WritePhoto("IMG_0004.JPG", 4000);
        var later = WritePhoto(@"Feriado\IMG_0005.JPG", 5000);

        var item = await ReviewAsync(Planned([.. _photos, added, later]), Listed(_photos));

        Assert.AreEqual("Refazer · 2 arquivos novos", item.StatusText);
        Assert.IsTrue(item.IsIncluded);
        Assert.IsNull(item.Caution);
    }

    [TestMethod]
    public async Task AFileWrittenAfterTheSetIsChanged()
    {
        File.WriteAllBytes(_photos[1], new byte[2500]);
        File.SetLastWriteTimeUtc(_photos[1], SetMadeAt.AddDays(3));
        File.SetLastWriteTimeUtc(_photos[2], SetMadeAt.AddDays(3));

        var item = await ReviewAsync(Planned(_photos), Listed(_photos));

        Assert.AreEqual("Refazer · 2 arquivos alterados", item.StatusText, "A new size or only a new date: both edited.");
        Assert.IsTrue(item.IsIncluded);
    }

    [TestMethod]
    public async Task AFileOfAnotherSizeButNotWrittenSinceLooksDamagedSoTheSetWaitsForTheUser()
    {
        File.WriteAllBytes(_photos[0], new byte[400]);
        File.SetLastWriteTimeUtc(_photos[0], TakenAt);

        var item = await ReviewAsync(Planned(_photos), Listed(_photos));

        Assert.AreEqual("Refazer · 1 arquivo alterado", item.StatusText);
        Assert.IsFalse(item.IsIncluded);
        StringAssert.Contains(item.Caution, "use Verificar e reparar antes");
    }

    [TestMethod]
    public async Task AFileGoneFromTheDiskIsRemovedAndTheSetWaitsForTheUser()
    {
        var gone = Path.Combine(_november, "IMG_0000.JPG");

        var item = await ReviewAsync(Planned(_photos), Listed([.. _photos, gone]));

        Assert.AreEqual("Refazer · 1 arquivo removido", item.StatusText);
        Assert.IsFalse(item.IsIncluded);
        StringAssert.Contains(item.Caution, "o conjunto atual pode recuperá-los");

        item.IsIncluded = true;

        Assert.AreEqual("Refazer · 1 arquivo removido", item.StatusText, "The box alone says it will be made.");
    }

    [TestMethod]
    public async Task FilesTheNewSetLeavesToOtherSetsSayHowMuchEachCovers()
    {
        // Made as one set for everything; now per folder, "Feriado" gets a set of its own.
        var holiday = WritePhoto(@"Feriado\IMG_0005.JPG", 5000);

        var item = await ReviewAsync(Planned(_photos), Listed([.. _photos, holiday]));

        Assert.AreEqual("Refazer · o atual protege 4 arquivos e o novo protegerá 3", item.StatusText);
        Assert.IsTrue(item.IsIncluded, "No file goes unprotected by it.");
    }

    [TestMethod]
    public async Task EveryReasonShowsTogether()
    {
        var added = WritePhoto("IMG_0004.JPG", 4000);
        File.SetLastWriteTimeUtc(_photos[0], SetMadeAt.AddDays(1));
        var gone = Path.Combine(_november, "IMG_0000.JPG");

        var item = await ReviewAsync(Planned([.. _photos, added]), Listed([.. _photos, gone]));

        Assert.AreEqual("Refazer · 1 arquivo novo, 1 arquivo alterado, 1 arquivo removido", item.StatusText);
    }

    [TestMethod]
    public async Task ASetTheEngineCannotReadSaysSoAndWaitsForTheUser()
    {
        File.WriteAllBytes(Index, new byte[10]);
        var item = new ReviewedSet(Planned(_photos))
        {
            Comparison = await SetComparison.CompareAsync(
                Planned(_photos), [Index], (_, _) => throw new EngineListingException(1, "valid file is not found"), _testContext.CancellationToken),
        };

        Assert.AreEqual("Refazer · não foi possível ler o conjunto atual", item.StatusText);
        Assert.AreEqual("valid file is not found", item.Comparison.Details);
        Assert.AreEqual("O motivo está em Detalhes técnicos.", item.Caution);
        Assert.IsFalse(item.IsIncluded);
    }

    [TestMethod]
    public async Task TheSetIsListedThroughItsRecoveryFileWhenTheIndexIsGone()
    {
        var recoveryFile = Path.Combine(_november, "Novembro.vol0+3.par2");
        File.WriteAllBytes(recoveryFile, new byte[10]);
        File.SetLastWriteTimeUtc(recoveryFile, SetMadeAt);
        string? asked = null;

        var comparison = await SetComparison.CompareAsync(
            Planned(_photos),
            [recoveryFile],
            (file, _) =>
            {
                asked = file;
                return Task.FromResult(Listed(_photos));
            },
            _testContext.CancellationToken);

        Assert.AreEqual(recoveryFile, asked);
        Assert.AreEqual(SetState.UpToDate, comparison.State);
    }

    [TestMethod]
    public void AnItemCanBeIncludedOrLeftOutOnlyOnceCompared()
    {
        var item = new ReviewedSet(Planned(_photos));

        Assert.IsFalse(item.IsCompared);
        Assert.AreEqual("Conferindo o conjunto atual…", item.StatusText);
    }

    /// <summary>Compares the set as it would be made of <paramref name="planned"/> with the set "Novembro.par2" listing <paramref name="listed"/>; none when null.</summary>
    private async Task<ReviewedSet> ReviewAsync(PlannedSet planned, SetListing? listed)
    {
        IReadOnlyList<string> setFiles = [];
        if (listed is not null)
        {
            File.WriteAllBytes(Index, new byte[10]);
            File.SetLastWriteTimeUtc(Index, SetMadeAt);
            setFiles = [Index];
        }

        var comparison = await SetComparison.CompareAsync(planned, setFiles, (_, _) => Task.FromResult(listed!), _testContext.CancellationToken);
        return new ReviewedSet(planned) { Comparison = comparison };
    }

    private PlannedSet Planned(string[] files) =>
        new(Index, _november, files, files.Sum(file => new FileInfo(file).Length));

    /// <summary>What the engine lists for a set made of <paramref name="files"/> as they were when taken.</summary>
    private SetListing Listed(string[] files) =>
        new(_november, files.ToDictionary(file => file, file => (long)SizeWhenTaken(file), StringComparer.OrdinalIgnoreCase));

    private static int SizeWhenTaken(string file) => int.Parse(Path.GetFileNameWithoutExtension(file)[^1..], System.Globalization.CultureInfo.InvariantCulture) * 1000;

    private string WritePhoto(string name, int size)
    {
        var path = Path.Combine(_november, name);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, TakenAt);
        return path;
    }
}
