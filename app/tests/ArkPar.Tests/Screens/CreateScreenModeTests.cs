// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;
using ArkPar.Screens;

namespace ArkPar.Tests.Screens;

/// <summary>
/// The Create screen making several sets at once, over a real temporary folder and a fake engine:
/// "07 - Viagem Bahia" with a photo, and "Praia do Forte" inside it with another.
/// </summary>
[TestClass]
public sealed class CreateScreenModeTests : IDisposable
{
    private static readonly RecoveryEstimate Trip = new(BlockSize: 20000, SourceBlocks: 100, RecoveryBlocks: 10, RecoveryFiles: 1, TotalSize: 210_000);
    private static readonly RecoveryEstimate Beach = new(BlockSize: 40000, SourceBlocks: 100, RecoveryBlocks: 12, RecoveryFiles: 2, TotalSize: 490_000);

    private readonly FakeEngine _engine = new();
    private readonly FakeTrial _trial = new();
    private readonly FakeListing _listing = new();
    private readonly List<string> _recycled = [];
    private readonly string _root;
    private readonly string _trip;
    private readonly string _beach;
    private readonly CreateScreen _screen;

    public CreateScreenModeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ArkPar.Tests", Guid.NewGuid().ToString("N"));
        _trip = Path.Combine(_root, "07 - Viagem Bahia");
        _beach = Path.Combine(_trip, "Praia do Forte");
        Directory.CreateDirectory(_beach);
        File.WriteAllBytes(Path.Combine(_trip, "IMG_4469.JPG"), new byte[2_000_000]);
        File.WriteAllBytes(Path.Combine(_beach, "IMG_4501.JPG"), new byte[4_000_000]);
        _screen = new CreateScreen(_engine.Start, _trial.RunAsync, _listing.ListAsync, Recycle);
    }

    public void Dispose()
    {
        _screen.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void TheFourModesAreOfferedWithAnExplanationAndTheExample()
    {
        Assert.AreEqual(
            "Um conjunto para tudo, Um por subpasta, Um por pasta, Um por arquivo",
            string.Join(", ", CreateScreen.Modes.Select(mode => mode.Title)));
        Assert.AreEqual(CreationMode.SingleSet, _screen.Mode);
        foreach (var mode in CreateScreen.Modes)
        {
            StringAssert.Contains(mode.Explanation, "Ex.:", $"{mode.Title} shows the example.");
        }

        StringAssert.Contains(CreateScreen.Modes[2].Explanation, "Praia do Forte");
    }

    [TestMethod]
    public async Task ChoosingAModeShowsEachSetAndWhereItGoesBeforeCreating()
    {
        await _screen.AddAsync([_trip]);
        Assert.AreEqual("1 conjunto será criado", _screen.SetsTitle);

        _screen.Mode = CreationMode.PerFolder;

        Assert.AreEqual("2 conjuntos serão criados", _screen.SetsTitle);
        Assert.AreEqual(
            @"07 - Viagem Bahia em 07 - Viagem Bahia (1 arquivo · 1,9 MB); Praia do Forte em 07 - Viagem Bahia\Praia do Forte (1 arquivo · 3,8 MB)",
            string.Join("; ", _screen.Sets.Select(set => $"{set.Name} em {set.Where} ({set.Meta})")));
        Assert.AreEqual("2 pastas", _screen.SaveIn);
        Assert.AreEqual($"2 conjuntos em {_trip}", _screen.Destination);
    }

    [TestMethod]
    public async Task EverySetIsEstimatedWithTheSameSettingsBeforeCreating()
    {
        await _screen.AddAsync([_trip]);
        _trial.Last.Finish(Trip);
        _screen.Redundancy = 30;
        _trial.Last.Finish(Trip);

        _screen.Mode = CreationMode.PerFolder;

        var trials = _trial.Trials.Skip(2).ToList();
        Assert.HasCount(2, trials, "One trial per set.");
        Assert.AreEqual(@"07 - Viagem Bahia.par2, Praia do Forte.par2", string.Join(", ", trials.Select(trial => Path.GetFileName(trial.Create.RecoveryFile))));
        Assert.IsTrue(trials.All(trial => trial.Create.RedundancyPercent == 30m), "The redundancy holds for every set.");
        Assert.AreEqual("Calculando… 0 de 2 conjuntos", _screen.EstimatingText);

        trials[0].Finish(Trip);

        Assert.AreEqual("Calculando… 1 de 2 conjuntos", _screen.EstimatingText);
        Assert.IsFalse(_screen.CanCreate, "Not before every set is estimated.");

        trials[1].Finish(Beach);

        Assert.IsTrue(_screen.CanCreate);
        Assert.AreEqual("≈ 684 KB", _screen.SpaceText, "Both sets together.");
        Assert.AreEqual("≈ 10% de cada conjunto", _screen.RepairableText, "The least of 200 KB of 2 MB and 480 KB of 4 MB.");
        Assert.AreEqual("200 blocos em 2 conjuntos, 22 para recuperação", _screen.BlocksText);
        Assert.AreEqual("3 arquivos de recuperação, além de 2 índices", _screen.RecoveryFilesText);
        Assert.IsNull(_screen.ParFilesWarning, "Only the per file mode warns.");
    }

    [TestMethod]
    public async Task PerFileSaysHowManyPar2FilesWillAppear()
    {
        await _screen.AddAsync([_trip]);
        _trial.Last.Finish(Trip);

        _screen.Mode = CreationMode.PerFile;
        Assert.IsNull(_screen.ParFilesWarning, "Not before the engine has said.");
        _trial.Trials[^2].Finish(Trip);
        _trial.Trials[^1].Finish(Beach);

        Assert.AreEqual("Vão aparecer 5 arquivos .par2 nas pastas, ao lado dos arquivos protegidos.", _screen.ParFilesWarning);
        Assert.AreEqual("IMG_4469.JPG, IMG_4501.JPG", string.Join(", ", _screen.Sets.Select(set => set.Name)));
    }

    [TestMethod]
    public async Task ChangingTheModeDuringAnEstimateStopsIt()
    {
        await _screen.AddAsync([_trip]);
        _screen.Mode = CreationMode.PerFolder;
        var perFolder = _trial.Trials.Skip(1).ToList();

        _screen.Mode = CreationMode.SingleSet;

        Assert.IsTrue(perFolder.All(trial => trial.Stop.IsCancellationRequested), "Its answers are not wanted.");
        _trial.Last.Finish(Trip);
        Assert.IsTrue(_screen.CanCreate);
        Assert.AreEqual("≈ 205 KB", _screen.SpaceText);
    }

    [TestMethod]
    public async Task SetsAreMadeOneAfterAnotherShowingWhichOne()
    {
        await ReadyAsync(CreationMode.PerFolder);

        var creating = _screen.CreateAsync();

        Assert.AreEqual("Conjunto 1 de 2: 07 - Viagem Bahia", _screen.SetProgressText);
        var first = (EngineCommand.Create)_engine.Last.Command;
        Assert.AreEqual(Path.Combine(_trip, "07 - Viagem Bahia.arkpar-novo.par2"), first.RecoveryFile, "Its draft.");
        Assert.AreEqual(_trip, first.BaseDirectory);
        Assert.AreEqual(Path.Combine(_trip, "IMG_4469.JPG"), first.SourceFiles.Single());
        _engine.Last.WriteSet();
        _engine.Last.Finish(new EngineResult(EngineOutcome.Completed, 0, null, "Created successfully"));
        await WaitUntilAsync(() => _engine.Runs.Count == 2);

        Assert.AreEqual("Conjunto 2 de 2: Praia do Forte", _screen.SetProgressText);
        var second = (EngineCommand.Create)_engine.Last.Command;
        Assert.AreEqual(_beach, second.BaseDirectory);
        Assert.AreEqual(first.Options, second.Options, "The Avançado settings hold for every set.");
        Assert.AreEqual(first.RedundancyPercent, second.RedundancyPercent);
        _engine.Last.WriteSet();
        _engine.Last.Finish(new EngineResult(EngineOutcome.Completed, 0, null, "Created successfully"));
        await creating;

        Assert.AreEqual(CreateStage.Created, _screen.Stage);
        Assert.AreEqual("Conjuntos criados", _screen.Heading);
        Assert.AreEqual(
            new Banner(BannerKind.Success, "Os arquivos de recuperação protegem 2 arquivos · 5,7 MB em 2 conjuntos.", $"4 arquivos · 1 KB, em {_trip}"),
            _screen.Banner);
        Assert.IsNull(_screen.SetProgressText);
    }

    [TestMethod]
    public async Task CancelingKeepsTheSetsAlreadyMade()
    {
        await ReadyAsync(CreationMode.PerFolder);
        var creating = _screen.CreateAsync();
        _engine.Last.WriteSet();
        _engine.Last.Finish(new EngineResult(EngineOutcome.Completed, 0, null, "Created successfully"));
        await WaitUntilAsync(() => _engine.Runs.Count == 2);

        _listing.Lists(Path.Combine(_trip, "07 - Viagem Bahia.par2"), Path.Combine(_trip, "IMG_4469.JPG"));
        _screen.Cancel();
        Assert.IsTrue(_engine.Last.CancelRequested);
        _engine.Last.WriteSet();
        _engine.Last.Finish(new EngineResult(EngineOutcome.Canceled, 2, null, "Cancel"));
        await creating;

        Assert.AreEqual(CreateStage.Canceled, _screen.Stage);
        Assert.AreEqual(new Banner(BannerKind.Warning, "1 de 2 conjuntos ficou pronto; a criação foi cancelada.", Detail: null), _screen.Banner);
        Assert.HasCount(2, Directory.GetFiles(_trip, "*.par2"), "The set made stays.");
        Assert.IsEmpty(Directory.GetFiles(_beach, "*.par2"), "The half-written one goes.");
        await WaitUntilAsync(() => !_screen.IsReviewing);
        Assert.AreEqual("Pular · em dia; Criar · sem conjunto", string.Join("; ", _screen.Items.Select(item => item.StatusText)), "Running again makes only the one canceled.");
    }

    [TestMethod]
    public async Task CancelingBetweenTwoSetsStartsNoOther()
    {
        File.WriteAllBytes(Path.Combine(_trip, "07 - Viagem Bahia.par2"), new byte[10]);
        using var recycling = new ManualResetEventSlim();
        using var screen = new CreateScreen(_engine.Start, _trial.RunAsync, _listing.ListAsync, files =>
        {
            recycling.Wait(TimeSpan.FromSeconds(10));
            Recycle(files);
        });
        await ReadyAsync(CreationMode.PerFolder, screen);
        var creating = screen.CreateAsync();
        _engine.Last.WriteSet();
        _engine.Last.Finish(new EngineResult(EngineOutcome.Completed, 0, null, "Created successfully"));
        await WaitUntilAsync(() => screen.ActivityText == "Levando o conjunto anterior para a Lixeira");

        Assert.IsTrue(screen.CanCancel, "Cancel waits for nothing.");
        screen.Cancel();
        recycling.Set();
        await creating;

        Assert.HasCount(1, _engine.Runs);
        Assert.AreEqual(CreateStage.Canceled, screen.Stage);
        Assert.AreEqual(new Banner(BannerKind.Warning, "1 de 2 conjuntos ficou pronto; a criação foi cancelada.", Detail: null), screen.Banner);
    }

    [TestMethod]
    public async Task AFailureStopsTheRestAndKeepsTheSetsAlreadyMade()
    {
        await ReadyAsync(CreationMode.PerFolder);
        var creating = _screen.CreateAsync();
        _engine.Last.WriteSet();
        _engine.Last.Finish(new EngineResult(EngineOutcome.Completed, 0, null, "Created successfully"));
        await WaitUntilAsync(() => _engine.Runs.Count == 2);

        _engine.Last.Finish(new EngineResult(EngineOutcome.Error, 1, null, "0x70, Espaço insuficiente no disco."));
        await creating;

        Assert.AreEqual(CreateStage.Failed, _screen.Stage);
        Assert.AreEqual(
            new Banner(BannerKind.Error, "O motor não conseguiu criar os arquivos de recuperação.", "Parou no conjunto \"Praia do Forte\" (2 de 2); 1 ficou pronto. Confira se há espaço no disco e se a pasta permite gravar."),
            _screen.Banner);
        Assert.AreEqual("0x70, Espaço insuficiente no disco.", _screen.Details);
        Assert.HasCount(2, Directory.GetFiles(_trip, "*.par2"));
    }

    [TestMethod]
    public async Task SetsAlreadyThereAreCountedInTheWarning()
    {
        File.WriteAllBytes(Path.Combine(_trip, "07 - Viagem Bahia.par2"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_beach, "Praia do Forte.vol0+3.par2"), new byte[10]);
        await _screen.AddAsync([_trip]);

        _screen.Mode = CreationMode.PerFolder;
        await WaitUntilAsync(() => !_screen.IsReviewing);
        foreach (var item in _screen.Items)
        {
            item.IsIncluded = true;
        }

        Assert.AreEqual("2 destes conjuntos já existem e serão substituídos. Cada um só sai da pasta depois que o novo ficar pronto, e vai para a Lixeira.", _screen.ReplaceWarning);
        Assert.AreEqual("Substituir arquivos de recuperação", _screen.CreateText);

        _screen.Items[1].IsIncluded = false;

        Assert.AreEqual("1 destes conjuntos já existe e será substituído. Ele só sai da pasta depois que o novo ficar pronto, e vai para a Lixeira.", _screen.ReplaceWarning);

        _screen.Mode = CreationMode.PerFile;

        Assert.IsNull(_screen.ReplaceWarning);
    }

    private Task ReadyAsync(CreationMode mode) => ReadyAsync(mode, _screen);

    /// <summary>The photos chosen in <paramref name="mode"/>, every set estimated and included, even one up to date.</summary>
    private async Task ReadyAsync(CreationMode mode, CreateScreen screen)
    {
        await screen.AddAsync([_trip]);
        screen.Mode = mode;
        foreach (var trial in _trial.Trials.Where(trial => !trial.Answer.IsCompleted))
        {
            trial.Finish(Trip);
        }

        await WaitUntilAsync(() => !screen.IsReviewing);
        foreach (var item in screen.Items)
        {
            item.IsIncluded = true;
        }

        Assert.IsTrue(screen.CanCreate);
    }

    /// <summary>What the Recycle Bin does: the files leave the folder.</summary>
    private void Recycle(IReadOnlyList<string> files)
    {
        _recycled.AddRange(files);
        Array.ForEach([.. files], File.Delete);
    }

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
