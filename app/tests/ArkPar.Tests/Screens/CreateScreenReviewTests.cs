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
    private readonly string _root;
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
        _screen = new CreateScreen(_engine.Start, _trial.RunAsync, _listing.ListAsync, files => Array.ForEach([.. files], File.Delete));
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

        Assert.AreEqual("Pular · sem conjunto", december.StatusText);
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
        Assert.AreEqual("Pular · não foi possível ler o conjunto atual", october.StatusText);
        Assert.AreEqual("O motivo está em Detalhes técnicos.", october.Caution);
        Assert.AreEqual($"{index}:\nvalid file is not found", _screen.Details);
    }

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
