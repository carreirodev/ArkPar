// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;
using ArkPar.Screens;

namespace ArkPar.Tests.Screens;

/// <summary>
/// The Create screen, driven as the user would, over a real temporary folder and a fake engine:
/// "07 - Viagem Bahia" with two photos and a subfolder.
/// </summary>
[TestClass]
public sealed class CreateScreenTests : IDisposable
{
    // What the engine's trial says for 10% (Engine/Fixtures/trial.txt).
    private static readonly RecoveryEstimate TenPercent = new(BlockSize: 25000, SourceBlocks: 240, RecoveryBlocks: 24, RecoveryFiles: 1, TotalSize: 635216);

    private readonly FakeEngine _engine = new();
    private readonly FakeTrial _trial = new();
    private readonly List<string> _recycled = [];
    private readonly string _root;
    private readonly string _trip;
    private readonly CreateScreen _screen;

    public CreateScreenTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ArkPar.Tests", Guid.NewGuid().ToString("N"));
        _trip = Path.Combine(_root, "07 - Viagem Bahia");
        Directory.CreateDirectory(Path.Combine(_trip, "Praia do Forte"));
        File.WriteAllBytes(Path.Combine(_trip, "IMG_4469.JPG"), new byte[2_000_000]);
        File.WriteAllBytes(Path.Combine(_trip, "Praia do Forte", "IMG_4501.JPG"), new byte[4_000_000]);
        _screen = new CreateScreen(_engine.Start, _trial.RunAsync, _recycled.AddRange);
    }

    private string RecoveryFile => Path.Combine(_trip, "07 - Viagem Bahia.par2");

    public void Dispose()
    {
        _screen.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task DroppingAFolderShowsWhatTheSetCoversAndAsksHowBigItWillBe()
    {
        await _screen.AddAsync([_trip]);

        Assert.AreEqual(CreateStage.Ready, _screen.Stage);
        Assert.AreEqual("2 arquivos · 5,7 MB", _screen.Summary);
        Assert.AreEqual("07 - Viagem Bahia", _screen.Tree.Single().Name);
        Assert.AreEqual("07 - Viagem Bahia", _screen.SaveIn);
        Assert.AreEqual(RecoveryFile, _screen.RecoveryFile);
        Assert.AreEqual(10, _screen.Redundancy);
        Assert.AreEqual("10%", _screen.RedundancyText);

        var create = _trial.Last.Create;
        Assert.AreEqual(RecoveryFile, create.RecoveryFile);
        Assert.AreEqual(_trip, create.BaseDirectory);
        Assert.AreEqual(10m, create.RedundancyPercent);
        Assert.HasCount(2, create.SourceFiles);
        Assert.IsTrue(_screen.IsEstimating);
        Assert.IsFalse(_screen.CanCreate, "Not before the engine has said what it will make.");

        _trial.Last.Finish(TenPercent);

        Assert.IsFalse(_screen.IsEstimating);
        Assert.AreEqual("≈ 620 KB", _screen.SpaceText);
        Assert.AreEqual("≈ 586 KB", _screen.RepairableText);
        Assert.IsTrue(_screen.CanCreate);
        Assert.AreEqual("Criar arquivos de recuperação", _screen.CreateText);
    }

    [TestMethod]
    public async Task MovingTheSliderDuringAnEstimateShowsOnlyTheLatest()
    {
        await _screen.AddAsync([_trip]);

        _screen.Redundancy = 20;
        _screen.Redundancy = 30;

        Assert.HasCount(1, _trial.Trials, "One trial at a time.");
        Assert.AreEqual("30%", _screen.RedundancyText);

        _trial.Trials[0].Finish(TenPercent);

        Assert.IsNull(_screen.Estimate, "The answer for 10% is not shown under 30%.");
        Assert.HasCount(2, _trial.Trials, "One more, with the latest value only.");
        Assert.AreEqual(30m, _trial.Last.Create.RedundancyPercent);
        Assert.IsTrue(_screen.IsEstimating);

        var thirtyPercent = TenPercent with { RecoveryBlocks = 72, TotalSize = 1_851_000 };
        _trial.Last.Finish(thirtyPercent);

        Assert.AreEqual(thirtyPercent, _screen.Estimate);
        Assert.IsFalse(_screen.IsEstimating);
    }

    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(250, 100)]
    public void RedundancyStaysInRange(int value, int expected)
    {
        _screen.Redundancy = value;

        Assert.AreEqual(expected, _screen.Redundancy);
    }

    [TestMethod]
    public async Task AdvancedSettingsStartAutomaticAndReachTheEngine()
    {
        Assert.AreEqual("Automático", _screen.BlockSize.Text);
        Assert.IsNull(_screen.RecoveryFileCount);
        Assert.AreEqual("Automático", _screen.Memory.Text);
        Assert.AreEqual("64 KB, 256 KB, 1,0 MB, 4,0 MB, 16 MB", string.Join(", ", CreateScreen.BlockSizes.Skip(1)));
        Assert.AreEqual("Até 50% da memória livre", CreateScreen.MemoryLimits[4].Text);

        await _screen.AddAsync([_trip]);
        _trial.Last.Finish(TenPercent);
        _screen.BlockSize = CreateScreen.BlockSizes[3];
        _screen.RecoveryFileCount = 3;
        _screen.Memory = CreateScreen.MemoryLimits[4];
        _trial.Trials[1].Finish(TenPercent);

        Assert.AreEqual(new CreateOptions(BlockSize: 1048576, RecoveryFileCount: 3, MemoryEighths: 4), _trial.Last.Create.Options);
        Assert.AreEqual("240 blocos de 24 KB, 24 para recuperação", _screen.BlocksText);
        Assert.AreEqual("1 arquivo de recuperação, além do índice", _screen.RecoveryFilesText);
    }

    [TestMethod]
    public async Task AnEstimateTheEngineCannotMakeBlocksCreating()
    {
        await _screen.AddAsync([_trip]);

        _trial.Last.Fail(new EngineTrialException(1, "input file is not found, IMG_4469.JPG"));

        Assert.AreEqual("Não foi possível calcular o tamanho com estes ajustes.", _screen.EstimateProblem);
        Assert.AreEqual("input file is not found, IMG_4469.JPG", _screen.Details);
        Assert.AreEqual("—", _screen.SpaceText);
        Assert.IsFalse(_screen.CanCreate);
    }

    [TestMethod]
    public async Task ChoosingNothingToProtectNeitherEstimatesNorCreates()
    {
        var empty = Path.Combine(_root, "vazia");
        Directory.CreateDirectory(empty);

        await _screen.AddAsync([empty]);

        Assert.AreEqual("Não há arquivos para proteger no que foi escolhido.", _screen.SelectionProblem);
        Assert.IsEmpty(_trial.Trials);
        Assert.IsFalse(_screen.CanCreate);
    }

    [TestMethod]
    public async Task AddingMoreKeepsWhatWasChosenAndClearingForgetsIt()
    {
        var other = Path.Combine(_root, "Outra viagem");
        Directory.CreateDirectory(other);
        File.WriteAllBytes(Path.Combine(other, "IMG_9000.JPG"), new byte[100]);

        await _screen.AddAsync([_trip]);
        await _screen.AddAsync([other]);

        Assert.AreEqual("07 - Viagem Bahia, Outra viagem", string.Join(", ", _screen.Tree.Select(node => node.Name)));
        Assert.AreEqual(_root, _screen.Selection.BaseDirectory);
        Assert.IsTrue(_trial.Trials[0].Stop.IsCancellationRequested, "The estimate for the first folder alone is not wanted.");

        _screen.Clear();

        Assert.AreEqual(CreateStage.Idle, _screen.Stage);
        Assert.IsEmpty(_screen.Tree);
        Assert.AreEqual("", _screen.Summary);
        Assert.IsFalse(_screen.CanCreate);
    }

    [TestMethod]
    public async Task CreatingShowsProgressAndCanBePausedAndCanceled()
    {
        await ReadyAsync();

        var creating = _screen.CreateAsync();

        var create = (EngineCommand.Create)_engine.Last.Command;
        Assert.AreEqual(_trial.Last.Create with { SourceFiles = create.SourceFiles }, create, "The create is the very one estimated.");
        CollectionAssert.AreEqual(_trial.Last.Create.SourceFiles.ToArray(), create.SourceFiles.ToArray());
        Assert.AreEqual(CreateStage.Creating, _screen.Stage);
        Assert.AreEqual("Criando", _screen.Heading);
        Assert.AreEqual("Preparando", _screen.ActivityText);
        Assert.IsTrue(_screen.IsPercentUnknown);
        Assert.IsFalse(_screen.CanChoose);

        _engine.Last.Report("Creating recovery slice", 42.5);
        Assert.AreEqual("Criando os blocos de recuperação", _screen.ActivityText);
        Assert.AreEqual(42.5, _screen.Percent);

        _screen.TogglePause();
        Assert.IsTrue(_engine.Last.IsPaused);
        Assert.AreEqual("Continuar", _screen.PauseText);

        _screen.Cancel();
        Assert.IsTrue(_engine.Last.CancelRequested);

        _engine.Last.Finish(new EngineResult(EngineOutcome.Canceled, 2, null, "Cancel"));
        await creating;
    }

    [TestMethod]
    public async Task CreatedSetSaysWhatItProtectsAndWhere()
    {
        await ReadyAsync();
        var creating = _screen.CreateAsync();

        File.WriteAllBytes(RecoveryFile, new byte[5648]);
        File.WriteAllBytes(Path.Combine(_trip, "07 - Viagem Bahia.vol0+24.par2"), new byte[629568]);
        _engine.Last.Finish(new EngineResult(EngineOutcome.Completed, 0, null, "Created successfully"));
        await creating;

        Assert.AreEqual(CreateStage.Created, _screen.Stage);
        Assert.AreEqual("Conjunto criado", _screen.Heading);
        Assert.AreEqual(new Banner(BannerKind.Success, "Os arquivos de recuperação protegem 2 arquivos · 5,7 MB.", $"2 arquivos · 620 KB, em {_trip}"), _screen.Banner);
        Assert.IsTrue(_engine.Last.Disposed);
        Assert.AreEqual("Substituir arquivos de recuperação", _screen.CreateText, "Creating again replaces the set just made.");
    }

    [TestMethod]
    public async Task ASetOfTheSameNameGoesToTheRecycleBinFirst()
    {
        var replacedIndex = Path.Combine(_trip, "07 - Viagem Bahia.par2");
        var replacedRecoveryFile = Path.Combine(_trip, "07 - Viagem Bahia.vol0+19.par2");
        File.WriteAllBytes(replacedIndex, new byte[10]);
        File.WriteAllBytes(replacedRecoveryFile, new byte[10]);

        await ReadyAsync();

        Assert.IsTrue(_screen.ReplacesExisting);
        Assert.AreEqual("Substituir arquivos de recuperação", _screen.CreateText);
        Assert.AreEqual("Já existe um conjunto com este nome nesta pasta. Os arquivos dele irão para a Lixeira.", _screen.ReplaceWarning);
        Assert.HasCount(2, _trial.Last.Create.SourceFiles, "The replaced set's files are not protected by the new one.");

        var creating = _screen.CreateAsync();
        await WaitUntilAsync(() => _engine.Runs.Count == 1);

        CollectionAssert.AreEqual(new[] { replacedIndex, replacedRecoveryFile }, _recycled);
        _engine.Last.Finish(new EngineResult(EngineOutcome.Completed, 0, null, "Created successfully"));
        await creating;
    }

    [TestMethod]
    public async Task CanceledCreateLeavesNoRecoveryFiles()
    {
        await ReadyAsync();
        var creating = _screen.CreateAsync();

        // Left behind by an engine ended before it could clean up (cancel grace period over).
        File.WriteAllBytes(RecoveryFile, new byte[100]);
        File.WriteAllBytes(Path.Combine(_trip, "07 - Viagem Bahia.vol0+24.par2"), new byte[100]);
        _engine.Last.Finish(new EngineResult(EngineOutcome.Canceled, 2, null, "Cancel"));
        await creating;

        Assert.AreEqual(CreateStage.Canceled, _screen.Stage);
        Assert.AreEqual("Criação cancelada", _screen.Heading);
        Assert.AreEqual(new Banner(BannerKind.Warning, "Nenhum arquivo de recuperação foi gravado.", Detail: null), _screen.Banner);
        Assert.IsEmpty(Directory.GetFiles(_trip, "*.par2"));
        Assert.IsTrue(File.Exists(Path.Combine(_trip, "IMG_4469.JPG")), "The photos stay.");
        Assert.IsTrue(_screen.CanChoose);
    }

    [TestMethod]
    public async Task EngineFailureShowsWhatItSaid()
    {
        await ReadyAsync();
        var creating = _screen.CreateAsync();

        _engine.Last.Finish(new EngineResult(EngineOutcome.Error, 1, null, "0x70, Espaço insuficiente no disco."));
        await creating;

        Assert.AreEqual(CreateStage.Failed, _screen.Stage);
        Assert.AreEqual("Não foi possível criar", _screen.Heading);
        Assert.AreEqual(BannerKind.Error, _screen.Banner?.Kind);
        Assert.AreEqual("0x70, Espaço insuficiente no disco.", _screen.Details);
    }

    [TestMethod]
    public async Task PauseAndCancelWaitForTheEngineWhileTheReplacedSetGoesToTheRecycleBin()
    {
        WriteSetOfTheSameName();
        using var recycling = new ManualResetEventSlim();
        using var screen = new CreateScreen(_engine.Start, _trial.RunAsync, files => recycling.Wait(TimeSpan.FromSeconds(10)));
        await ReadyAsync(screen);

        var creating = screen.CreateAsync();

        Assert.AreEqual("Levando o conjunto anterior para a Lixeira", screen.ActivityText);
        Assert.IsFalse(screen.CanControl);
        recycling.Set();
        await WaitUntilAsync(() => _engine.Runs.Count == 1);
        Assert.IsTrue(screen.CanControl);

        _engine.Last.Finish(new EngineResult(EngineOutcome.Completed, 0, null, "Created successfully"));
        await creating;
        Assert.IsFalse(screen.CanControl);
    }

    [TestMethod]
    public async Task ClosingWhileTheReplacedSetGoesToTheRecycleBinStartsNothing()
    {
        WriteSetOfTheSameName();
        using var recycling = new ManualResetEventSlim();
        var screen = new CreateScreen(_engine.Start, _trial.RunAsync, files => recycling.Wait(TimeSpan.FromSeconds(10)));
        await ReadyAsync(screen);
        var creating = screen.CreateAsync();

        screen.Dispose();
        recycling.Set();
        await creating;

        Assert.IsEmpty(_engine.Runs, "No engine left running after the window closed.");
    }

    [TestMethod]
    public async Task APartialFileThatCannotBeDeletedIsReportedNotThrown()
    {
        await ReadyAsync();
        var creating = _screen.CreateAsync();
        var partial = Path.Combine(_trip, "07 - Viagem Bahia.vol0+24.par2");

        // Held by another program, as an antivirus scanning it would.
        using (new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            _engine.Last.Finish(new EngineResult(EngineOutcome.Canceled, 2, null, "Cancel"));
            await creating;
        }

        Assert.AreEqual(CreateStage.Canceled, _screen.Stage);
        StringAssert.Contains(_screen.Banner?.Detail, "apague os que começam com \"07 - Viagem Bahia\"");
        Assert.IsNotNull(_screen.Details);
    }

    [TestMethod]
    public async Task AfterAFailureTheScreenOffersReplaceOnlyIfASetIsStillThere()
    {
        WriteSetOfTheSameName();
        using var screen = new CreateScreen(_engine.Start, _trial.RunAsync, files => Array.ForEach([.. files], File.Delete));
        await ReadyAsync(screen);
        Assert.IsTrue(screen.ReplacesExisting);
        var creating = screen.CreateAsync();
        await WaitUntilAsync(() => _engine.Runs.Count == 1);

        _engine.Last.Fail(new InvalidOperationException("The engine's output could not be read."));
        await creating;

        Assert.AreEqual(CreateStage.Failed, screen.Stage);
        Assert.IsFalse(screen.ReplacesExisting, "The replaced set went to the Recycle Bin.");
        Assert.AreEqual("Criar arquivos de recuperação", screen.CreateText);
    }

    [TestMethod]
    public async Task AnEstimateThatFailsAtOnceDoesNotBlockTheNextOne()
    {
        // E.g. the engine is missing: the trial fails before it starts.
        var trials = 0;
        using var screen = new CreateScreen(_engine.Start, (create, stop) =>
        {
            trials++;
            return Task.FromException<RecoveryEstimate>(new FileNotFoundException("The engine (par2j64.exe) was not found."));
        }, _recycled.AddRange);
        await screen.AddAsync([_trip]);

        screen.Redundancy = 20;

        Assert.AreEqual(2, trials);
        Assert.IsFalse(screen.IsEstimating);
        Assert.AreEqual("Não foi possível calcular o tamanho com estes ajustes.", screen.EstimateProblem);
    }

    [TestMethod]
    public async Task AnEstimateThatWorksAgainClearsTheEarlierFailure()
    {
        await _screen.AddAsync([_trip]);
        _trial.Last.Fail(new EngineTrialException(1, "input file is not found, IMG_4469.JPG"));

        _screen.Redundancy = 20;
        _trial.Last.Finish(TenPercent);

        Assert.IsNull(_screen.EstimateProblem);
        Assert.IsNull(_screen.Details);
    }

    private void WriteSetOfTheSameName()
    {
        File.WriteAllBytes(Path.Combine(_trip, "07 - Viagem Bahia.par2"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_trip, "07 - Viagem Bahia.vol0+19.par2"), new byte[10]);
    }

    private Task ReadyAsync() => ReadyAsync(_screen);

    private async Task ReadyAsync(CreateScreen screen)
    {
        await screen.AddAsync([_trip]);
        _trial.Last.Finish(TenPercent);
        Assert.IsTrue(screen.CanCreate);
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
