// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;
using ArkPar.Screens;

namespace ArkPar.Tests.Screens;

/// <summary>
/// Running the create again over a folder that already has sets, over a real temporary folder and a fake engine:
/// "2026" with "capa.jpg" loose in it, "Outubro", "Novembro" (with "Feriado" inside) and "Dezembro", a photo each.
/// </summary>
[TestClass]
public sealed class CreateScreenReviewTests : IDisposable
{
    private static readonly RecoveryEstimate OneSet = new(BlockSize: 20000, SourceBlocks: 100, RecoveryBlocks: 10, RecoveryFiles: 1, TotalSize: 210_000);

    private readonly FakeEngine _engine = new();
    private readonly FakeTrial _trial = new();
    private readonly FakeListing _listing = new();
    private readonly List<string> _recycled = [];
    private readonly string _root;
    private bool _hasRecycleBin = true;
    private readonly CreateScreen _screen;

    public CreateScreenReviewTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ArkPar.Tests", Guid.NewGuid().ToString("N"), "2026");
        Directory.CreateDirectory(Path.Combine(_root, "Novembro", "Feriado"));
        Directory.CreateDirectory(Path.Combine(_root, "Outubro"));
        Directory.CreateDirectory(Path.Combine(_root, "Dezembro"));
        WritePhoto("capa.jpg");
        WritePhoto(@"Outubro\IMG_1001.JPG");
        WritePhoto(@"Novembro\IMG_1101.JPG");
        WritePhoto(@"Novembro\Feriado\IMG_1115.JPG");
        WritePhoto(@"Dezembro\IMG_1201.JPG");
        _screen = new CreateScreen(
            _engine.Start,
            _trial.RunAsync,
            _listing.ListAsync,
            files =>
            {
                _recycled.AddRange(files);
                Array.ForEach([.. files], File.Delete);
            },
            _ => _hasRecycleBin);
    }

    public void Dispose()
    {
        _screen.Dispose();
        Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
    }

    [TestMethod]
    public async Task TheListSaysForEachSetWhatTheCreateWillDoAndWhy()
    {
        WriteSet(@"Outubro\Outubro.par2", @"Outubro\IMG_1001.JPG");
        WriteSet(@"Novembro\Novembro.par2", @"Novembro\IMG_1101.JPG");

        await ReadyAsync(CreationMode.PerSubfolder);

        Assert.AreEqual(
            "2026: Criar · sem conjunto; Dezembro: Criar · sem conjunto; Novembro: Refazer · 1 arquivo novo; Outubro: Pular · em dia",
            string.Join("; ", _screen.Items.Select(item => $"{item.Planned.Name}: {item.StatusText}")));
        Assert.AreEqual("3 de 4 conjuntos serão criados", _screen.SetsTitle);
        Assert.AreEqual($"3 conjuntos em {_root}", _screen.Destination, "The sets included.");
        Assert.AreEqual("3 pastas", _screen.SaveIn);
        Assert.AreEqual("1 destes conjuntos já existe e será substituído. Ele só sai da pasta depois que o novo ficar pronto, e vai para a Lixeira.", _screen.ReplaceWarning);
        Assert.AreEqual("Criar arquivos de recuperação", _screen.CreateText);
        Assert.HasCount(3, _screen.Estimate!.Sets, "Only the sets included.");

        await CreateAllAsync();

        Assert.AreEqual("2026, Dezembro, Novembro", string.Join(", ", _engine.Runs.Select(run => SetNameOf(run))), "Outubro is skipped.");
        Assert.AreEqual(new Banner(BannerKind.Success, "Os arquivos de recuperação protegem 4 arquivos · 3,9 KB em 3 conjuntos.", $"6 arquivos · 1 KB, em {_root}"), _screen.Banner);
    }

    [TestMethod]
    public async Task LeavingASetOutTakesItOutOfTheEstimateAndTheCreate()
    {
        await ReadyAsync(CreationMode.PerSubfolder);
        Assert.AreEqual("4 conjuntos serão criados", _screen.SetsTitle);
        Assert.AreEqual("≈ 820 KB", _screen.SpaceText);

        var december = _screen.Items.Single(item => item.Planned.Name == "Dezembro");
        december.IsIncluded = false;

        Assert.AreEqual("Criar · sem conjunto", december.StatusText, "What it needs; the box says it is left out.");
        Assert.AreEqual("3 de 4 conjuntos serão criados", _screen.SetsTitle);
        Assert.AreEqual("≈ 615 KB", _screen.SpaceText, "Three sets of 205 KB.");
        await CreateAllAsync();
        Assert.IsFalse(_engine.Runs.Any(run => SetNameOf(run) == "Dezembro"));

        foreach (var item in _screen.Items)
        {
            item.IsIncluded = false;
        }

        Assert.AreEqual("Nenhum conjunto será criado", _screen.SetsTitle);
        Assert.AreEqual("—", _screen.SpaceText);
        Assert.IsFalse(_screen.CanCreate);
    }

    [TestMethod]
    [DataRow(CreationMode.SingleSet, "2026: Refazer · 1 arquivo novo")]
    [DataRow(CreationMode.PerSubfolder, "Novembro: Refazer · 1 arquivo novo")]
    [DataRow(CreationMode.PerFolder, "Novembro: Refazer · 1 arquivo novo")]
    [DataRow(CreationMode.PerFile, "IMG_1102.JPG: Criar · sem conjunto")]
    public async Task RunningAgainInAnyModeRemakesOnlyWhatChanged(CreationMode mode, string remade)
    {
        await ReadyAsync(mode);
        await CreateAllAsync();
        await WaitUntilAsync(() => !_screen.IsReviewing);

        Assert.IsTrue(_screen.Items.All(item => item.StatusText == "Pular · em dia"), "Nothing to do right after creating.");
        Assert.IsFalse(_screen.CanCreate);

        WritePhoto(@"Novembro\IMG_1102.JPG");
        await _screen.AddAsync([_root]);
        await WaitUntilAsync(() => !_screen.IsReviewing);

        Assert.AreEqual(remade, string.Join("; ", _screen.Items.Where(item => item.IsIncluded).Select(item => $"{item.Planned.Name}: {item.StatusText}")));
    }

    [TestMethod]
    public async Task ASetThatCoveredMoreSaysHowMuchEachCovers()
    {
        // Made in January as one set for everything; in March, per subfolder, "2026.par2" covers only the cover.
        WriteSet("2026.par2", "capa.jpg", @"Outubro\IMG_1001.JPG", @"Novembro\IMG_1101.JPG", @"Novembro\Feriado\IMG_1115.JPG", @"Dezembro\IMG_1201.JPG");

        await ReadyAsync(CreationMode.PerSubfolder);

        var cover = _screen.Items.Single(item => item.Planned.Name == "2026");
        Assert.AreEqual("Refazer · o atual protege 5 arquivos e o novo protegerá 1", cover.StatusText);
        Assert.IsTrue(cover.IsIncluded);
    }

    [TestMethod]
    public async Task CreatingWaitsUntilEverySetIsCompared()
    {
        WriteSet(@"Outubro\Outubro.par2", @"Outubro\IMG_1001.JPG");
        var lists = new TaskCompletionSource();
        _listing.Gate = lists.Task;

        await _screen.AddAsync([_root]);
        _screen.Mode = CreationMode.PerSubfolder;
        _trial.Trials.Where(trial => !trial.Answer.IsCompleted).ToList().ForEach(trial => trial.Finish(OneSet));

        Assert.IsTrue(_screen.IsReviewing);
        Assert.AreEqual("Conferindo os conjuntos que já existem… 3 de 4", _screen.ReviewingText);
        Assert.AreEqual("Conferindo o conjunto atual…", _screen.Items.Single(item => item.Planned.Name == "Outubro").StatusText);
        Assert.IsFalse(_screen.CanCreate);

        lists.SetResult();
        await WaitUntilAsync(() => !_screen.IsReviewing);

        Assert.IsTrue(_screen.CanCreate);
        Assert.AreEqual("Pular · em dia", _screen.Items.Single(item => item.Planned.Name == "Outubro").StatusText);
    }

    [TestMethod]
    public async Task ASetFoundStaleOnceComparedIsAnnouncedToTheScreen()
    {
        WriteSet(@"Novembro\Novembro.par2", @"Novembro\IMG_1101.JPG");
        var lists = new TaskCompletionSource();
        _listing.Gate = lists.Task;
        await _screen.AddAsync([_root]);
        _screen.Mode = CreationMode.PerSubfolder;
        var announced = new List<string?>();
        _screen.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        lists.SetResult();
        await WaitUntilAsync(() => !_screen.IsReviewing);

        Assert.IsTrue(_screen.Items.Single(item => item.Planned.Name == "Novembro").IsIncluded, "Stale, so it stays included.");
        Assert.IsNotNull(_screen.ReplaceWarning);
        CollectionAssert.IsSubsetOf(new[] { nameof(CreateScreen.ReplaceWarning), nameof(CreateScreen.CreateText) }, announced, "The screen redraws the warning.");
    }

    [TestMethod]
    public async Task ChangingTheModeWhileComparingStopsTheComparing()
    {
        WriteSet(@"Outubro\Outubro.par2", @"Outubro\IMG_1001.JPG");
        var lists = new TaskCompletionSource();
        _listing.Gate = lists.Task;
        await _screen.AddAsync([_root]);
        _screen.Mode = CreationMode.PerSubfolder;
        var perSubfolder = _screen.Items;

        _screen.Mode = CreationMode.SingleSet;
        lists.SetResult();
        await Task.Delay(50);

        Assert.IsFalse(_screen.IsReviewing, "No set of the single set's name is there.");
        Assert.AreEqual("Conferindo o conjunto atual…", perSubfolder.Single(item => item.Planned.Name == "Outubro").StatusText, "Its answer is not wanted.");
        Assert.AreEqual("Criar · sem conjunto", _screen.Items.Single().StatusText);
    }

    [TestMethod]
    public async Task ASetThatCannotBeReadIsLeftOutWithTheReason()
    {
        var index = WriteSet(@"Outubro\Outubro.par2");
        _listing.Fails(index, "valid file is not found");

        await ReadyAsync(CreationMode.PerSubfolder);

        var october = _screen.Items.Single(item => item.Planned.Name == "Outubro");
        Assert.AreEqual("Refazer · não foi possível ler o conjunto atual", october.StatusText);
        Assert.AreEqual("O motivo está em Detalhes técnicos.", october.Caution);
        Assert.AreEqual($"{index}:\nvalid file is not found", _screen.Details);
    }

    [TestMethod]
    public async Task ASetWhosePhotoIsGoneIsAWarningTheCreateNeverMakes()
    {
        await ReadyAsync(CreationMode.PerFile);
        await CreateAllAsync();
        File.Delete(Path.Combine(_root, @"Novembro\IMG_1101.JPG"));
        var runs = _engine.Runs.Count;

        await _screen.AddAsync([_root]);
        await WaitUntilAsync(() => !_screen.IsReviewing && !_screen.IsFindingOrphanedSets);

        Assert.AreEqual(@"IMG_1101.JPG.par2 (2026\Novembro): 1 arquivo removido, use Verificar e reparar", Describe(_screen.OrphanedSets));
        Assert.IsTrue(_screen.HasOrphanedSets);
        Assert.IsFalse(_screen.Items.Any(item => item.Planned.Name == "IMG_1101.JPG"));
        Assert.AreEqual("Nenhum conjunto será criado", _screen.SetsTitle, "The other photos are protected and unchanged.");
        Assert.IsFalse(_screen.CanCreate);
        Assert.AreEqual(runs, _engine.Runs.Count);
    }

    [TestMethod]
    public async Task ASetInAFolderLeftWithNoFilesIsAWarning()
    {
        await ReadyAsync(CreationMode.PerFolder);
        await CreateAllAsync();
        File.Delete(Path.Combine(_root, @"Dezembro\IMG_1201.JPG"));

        await _screen.AddAsync([_root]);
        await WaitUntilAsync(() => !_screen.IsReviewing && !_screen.IsFindingOrphanedSets);

        Assert.AreEqual(@"Dezembro.par2 (2026\Dezembro): 1 arquivo removido, use Verificar e reparar", Describe(_screen.OrphanedSets));
        Assert.AreEqual("2026, Novembro, Feriado, Outubro", string.Join(", ", _screen.Items.Select(item => item.Planned.Name)));
    }

    [TestMethod]
    public async Task ASetOfAnotherModeIsAWarningOnlyWhenItLostFiles()
    {
        // In January one set per subfolder; in March, per file. Novembro's set still has its photos; January's does not.
        WriteSet(@"Novembro\Novembro.par2", @"Novembro\IMG_1101.JPG", @"Novembro\Feriado\IMG_1115.JPG");
        WritePhoto("IMG_0101.JPG");
        var january = WriteSet("Janeiro.par2", "capa.jpg", "IMG_0101.JPG");
        File.Delete(Path.Combine(_root, "IMG_0101.JPG"));
        File.WriteAllBytes(Path.Combine(_root, @"Outubro\Outubro.arkpar-novo.par2"), new byte[10]);

        await ReadyAsync(CreationMode.PerFile);
        await WaitUntilAsync(() => !_screen.IsFindingOrphanedSets);

        Assert.AreEqual("Janeiro.par2 (2026): 1 arquivo removido; ainda protege 1 que está no disco, use Verificar e reparar", Describe(_screen.OrphanedSets), "Novembro's set covers files still there.");
        Assert.AreEqual(january, _screen.OrphanedSets.Single().Path);
        Assert.Contains(Path.Combine(_root, @"Novembro\Novembro.par2"), _listing.Asked, "Listed to know.");
        Assert.IsFalse(_listing.Asked.Any(file => file.Contains(".arkpar-novo", StringComparison.Ordinal)), "A set being written is not one.");
        Assert.AreEqual("5 conjuntos serão criados", _screen.SetsTitle);
    }

    [TestMethod]
    public async Task CreatingDoesNotWaitForTheOtherSetsAndStopsLookingForThem()
    {
        WritePhoto("IMG_0101.JPG");
        WriteSet("Janeiro.par2", "IMG_0101.JPG");
        File.Delete(Path.Combine(_root, "IMG_0101.JPG"));
        var lists = new TaskCompletionSource();
        _listing.Gate = lists.Task;

        await ReadyAsync(CreationMode.PerFolder);

        Assert.IsTrue(_screen.IsFindingOrphanedSets);
        Assert.IsFalse(_screen.HasOrphanedSets);
        Assert.IsTrue(_screen.CanCreate, "No set of the folders' names is there: nothing to compare.");

        var creating = CreateAllAsync();
        Assert.IsFalse(_screen.IsFindingOrphanedSets, "The engine is the create's: the search stops, and starts again after it.");

        lists.SetResult();
        await creating;
        await WaitUntilAsync(() => !_screen.IsReviewing && !_screen.IsFindingOrphanedSets);

        Assert.AreEqual("Janeiro.par2 (2026): 1 arquivo removido, use Verificar e reparar", Describe(_screen.OrphanedSets));
    }

    [TestMethod]
    public async Task ChangingTheModeWhileFindingTheOtherSetsStartsAgain()
    {
        WritePhoto("IMG_0101.JPG");
        WriteSet("Janeiro.par2", "IMG_0101.JPG");
        File.Delete(Path.Combine(_root, "IMG_0101.JPG"));
        var lists = new TaskCompletionSource();
        _listing.Gate = lists.Task;
        await ReadyAsync(CreationMode.PerFolder);
        var announced = new List<string?>();
        _screen.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        _screen.Mode = CreationMode.PerSubfolder;
        lists.SetResult();
        await WaitUntilAsync(() => !_screen.IsFindingOrphanedSets);
        await Task.Delay(50);

        Assert.AreEqual("Janeiro.par2 (2026): 1 arquivo removido, use Verificar e reparar", Describe(_screen.OrphanedSets), "Once: the answer to the earlier list is not wanted.");
        Assert.Contains(nameof(CreateScreen.OrphanedSets), announced);
    }

    [TestMethod]
    public async Task AnOtherSetThatCannotBeReadIsAWarningWithTheReason()
    {
        var index = WriteSet("Janeiro.par2");
        _listing.Fails(index, "valid file is not found");

        await ReadyAsync(CreationMode.PerSubfolder);
        await WaitUntilAsync(() => !_screen.IsFindingOrphanedSets);

        Assert.AreEqual("Janeiro.par2 (2026): Não foi possível ler o conjunto; o motivo está em Detalhes técnicos", Describe(_screen.OrphanedSets));
        Assert.AreEqual($"{index}:\nvalid file is not found", _screen.Details);
    }

    [TestMethod]
    public async Task AFolderChosenWithNoFilesLeftStillShowsItsSet()
    {
        var december = Path.Combine(_root, "Dezembro");
        WriteSet(@"Dezembro\Dezembro.par2", @"Dezembro\IMG_1201.JPG");
        File.Delete(Path.Combine(december, "IMG_1201.JPG"));

        await _screen.AddAsync([december]);
        await WaitUntilAsync(() => !_screen.IsFindingOrphanedSets);

        Assert.IsNotNull(_screen.SelectionProblem);
        Assert.AreEqual("Dezembro.par2 (Dezembro): 1 arquivo removido, use Verificar e reparar", Describe(_screen.OrphanedSets));
    }

    [TestMethod]
    public async Task DeletingAnOrphanedSetSendsItsFilesToTheRecycleBinForGood()
    {
        var (index, volume) = WriteOrphanedSet("Janeiro");
        await ReadyAsync(CreationMode.PerFolder);
        await WaitUntilAsync(() => !_screen.IsFindingOrphanedSets);
        var january = _screen.OrphanedSets.Single();

        Assert.IsTrue(january.CanDelete);
        Assert.IsNull(_screen.DeleteQuestion([january]), "The Recycle Bin gives it back: nothing to ask.");
        Assert.IsNull(_screen.DeleteAllText, "One set: its own button is enough.");
        await _screen.DeleteOrphanedSetsAsync([january]);

        CollectionAssert.AreEquivalent(new[] { index, volume }, _recycled);
        Assert.IsFalse(_screen.HasOrphanedSets);
        Assert.IsFalse(_screen.HasBanner);

        await _screen.AddAsync([_root]);
        await WaitUntilAsync(() => !_screen.IsReviewing && !_screen.IsFindingOrphanedSets);
        Assert.IsFalse(_screen.HasOrphanedSets, "Gone: running again does not bring it back.");
    }

    [TestMethod]
    public async Task DeletingAllTheOrphanedSetsAtOnce()
    {
        WriteOrphanedSet("Janeiro");
        WriteOrphanedSet("Fevereiro");
        await ReadyAsync(CreationMode.PerFolder);
        await WaitUntilAsync(() => !_screen.IsFindingOrphanedSets);

        Assert.AreEqual("Apagar os 2 conjuntos", _screen.DeleteAllText);
        await _screen.DeleteOrphanedSetsAsync(_screen.OrphanedSets);

        Assert.HasCount(4, _recycled);
        Assert.IsFalse(_screen.HasOrphanedSets);
        Assert.IsNull(_screen.DeleteAllText);
    }

    [TestMethod]
    public async Task WithoutARecycleBinDeletingIsAskedFirstAndIsForGood()
    {
        _hasRecycleBin = false;
        var (index, volume) = WriteOrphanedSet("Janeiro");
        WriteOrphanedSet("Fevereiro");
        await ReadyAsync(CreationMode.PerFolder);
        await WaitUntilAsync(() => !_screen.IsFindingOrphanedSets);
        var january = _screen.OrphanedSets.Single(set => set.Name == "Janeiro.par2");

        Assert.AreEqual(("Apagar de vez?", "Este disco não tem Lixeira: Janeiro.par2 será apagado de vez, sem como recuperar."), _screen.DeleteQuestion([january]));
        Assert.AreEqual(("Apagar de vez?", "Este disco não tem Lixeira: os 2 conjuntos serão apagados de vez, sem como recuperar."), _screen.DeleteQuestion(_screen.OrphanedSets));
        await _screen.DeleteOrphanedSetsAsync([january]);

        Assert.IsEmpty(_recycled);
        Assert.IsFalse(File.Exists(index) || File.Exists(volume));
        Assert.AreEqual("Fevereiro.par2", _screen.OrphanedSets.Single().Name);
    }

    [TestMethod]
    public async Task ASetThatCannotBeDeletedStaysWithTheReason()
    {
        var (index, _) = WriteOrphanedSet("Janeiro");
        WriteOrphanedSet("Fevereiro");
        await ReadyAsync(CreationMode.PerFolder);
        await WaitUntilAsync(() => !_screen.IsFindingOrphanedSets);

        using (new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await _screen.DeleteOrphanedSetsAsync(_screen.OrphanedSets);
        }

        Assert.AreEqual("Janeiro.par2", _screen.OrphanedSets.Single().Name, "Fevereiro's went.");
        Assert.AreEqual(new Banner(BannerKind.Warning, "1 conjunto não pôde ser apagado.", "Algum arquivo dele pode estar aberto em outro programa. O motivo está em Detalhes técnicos."), _screen.Banner);
        StringAssert.Contains(_screen.Details, index);
        Assert.IsTrue(_screen.CanDeleteOrphanedSets, "It can be tried again.");
    }

    [TestMethod]
    public async Task DeletingASetThatStillProtectsFilesIsAskedFirst()
    {
        // In January one set for the cover and two photos; one photo is gone, the cover and IMG_0102 are still there.
        WritePhoto("IMG_0101.JPG");
        WritePhoto("IMG_0102.JPG");
        WriteSet("Janeiro.par2", "capa.jpg", "IMG_0101.JPG", "IMG_0102.JPG");
        File.Delete(Path.Combine(_root, "IMG_0101.JPG"));
        WriteOrphanedSet("Fevereiro");
        await ReadyAsync(CreationMode.PerFolder);
        await WaitUntilAsync(() => !_screen.IsFindingOrphanedSets);
        var january = _screen.OrphanedSets.Single(set => set.Name == "Janeiro.par2");
        var february = _screen.OrphanedSets.Single(set => set.Name == "Fevereiro.par2");

        Assert.AreEqual("1 arquivo removido; ainda protege 2 que estão no disco, use Verificar e reparar", january.StatusText);
        Assert.AreEqual(
            ("Apagar o conjunto?", "Janeiro.par2 ainda protege 2 arquivos que estão no disco, e eles ficam sem esse conjunto. Ele vai para a Lixeira."),
            _screen.DeleteQuestion([january]));
        Assert.AreEqual(
            ("Apagar os conjuntos?", "Entre eles, 1 ainda protege 2 arquivos que estão no disco, e eles ficam sem ele. Eles vão para a Lixeira."),
            _screen.DeleteQuestion(_screen.OrphanedSets));
        Assert.IsNull(_screen.DeleteQuestion([february]), "It protects nothing left, and the Recycle Bin gives it back.");

        _hasRecycleBin = false;
        Assert.AreEqual(
            ("Apagar de vez?", "Janeiro.par2 ainda protege 2 arquivos que estão no disco, e eles ficam sem esse conjunto. Este disco não tem Lixeira: Janeiro.par2 será apagado de vez, sem como recuperar."),
            _screen.DeleteQuestion([january]));
    }

    /// <summary>Writes a set of "2026" (index and one recovery file) covering a photo deleted since, so it is orphaned in any mode but single set.</summary>
    private (string Index, string Volume) WriteOrphanedSet(string name)
    {
        var photo = $"{name}.JPG";
        WritePhoto(photo);
        var index = WriteSet($"{name}.par2", photo);
        var volume = Path.Combine(_root, $"{name}.vol0+1.par2");
        File.WriteAllBytes(volume, new byte[10]);
        File.Delete(Path.Combine(_root, photo));
        return (index, volume);
    }

    private static string Describe(IEnumerable<OrphanedSet> sets) => string.Join("; ", sets.Select(set => $"{set.Name} ({set.Where}): {set.StatusText}"));

    /// <summary>Writes a set made before the photos changed, covering <paramref name="files"/> (paths under "2026").</summary>
    private string WriteSet(string index, params string[] files)
    {
        var path = Path.Combine(_root, index);
        File.WriteAllBytes(path, new byte[10]);
        _listing.Lists(path, [.. files.Select(file => Path.Combine(_root, file))]);
        return path;
    }

    private async Task ReadyAsync(CreationMode mode)
    {
        await _screen.AddAsync([_root]);
        _screen.Mode = mode;
        foreach (var trial in _trial.Trials.Where(trial => !trial.Answer.IsCompleted))
        {
            trial.Finish(OneSet);
        }

        await WaitUntilAsync(() => !_screen.IsReviewing);
        Assert.IsTrue(_screen.CanCreate);
    }

    /// <summary>Creates the sets included, the fake engine making each, which the engine's list then gives back.</summary>
    private async Task CreateAllAsync()
    {
        var finished = _engine.Runs.Count;
        var creating = _screen.CreateAsync();
        while (!creating.IsCompleted)
        {
            await WaitUntilAsync(() => creating.IsCompleted || _engine.Runs.Count > finished);
            if (_engine.Runs.Count > finished)
            {
                var run = _engine.Runs[finished++];
                var create = (EngineCommand.Create)run.Command;
                _listing.Lists(create.RecoveryFile.Replace(".arkpar-novo", "", StringComparison.Ordinal), [.. create.SourceFiles]);
                run.WriteSet();
                run.Finish(new EngineResult(EngineOutcome.Completed, 0, null, "Created successfully"));
            }
        }

        await creating;
        Assert.AreEqual(CreateStage.Created, _screen.Stage, _screen.Details);
    }

    private static string SetNameOf(FakeRun run) => Path.GetFileName(run.Command.RecoveryFile).Replace(".arkpar-novo.par2", "", StringComparison.Ordinal);

    private void WritePhoto(string name) => File.WriteAllBytes(Path.Combine(_root, name), new byte[1000]);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.IsLessThan(timeout, DateTime.UtcNow, "Timed out.");
            await Task.Delay(10);
        }
    }
}
