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
    private readonly FakeListing _listing = new();
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
        _screen = NewScreen(Recycle);
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
        Assert.AreEqual(RecoveryFile, _screen.Destination);
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

        Assert.AreEqual("30%", _screen.RedundancyText);
        Assert.IsTrue(_trial.Trials.SkipLast(1).All(trial => trial.Stop.IsCancellationRequested), "The answers for 10% and 20% are not wanted.");
        Assert.AreEqual(30m, _trial.Last.Create.RedundancyPercent);
        Assert.IsNull(_screen.Estimate);
        Assert.IsTrue(_screen.IsEstimating);

        var thirtyPercent = TenPercent with { RecoveryBlocks = 72, TotalSize = 1_851_000 };
        _trial.Last.Finish(thirtyPercent);

        Assert.AreEqual(thirtyPercent, _screen.Estimate?.Sets.Single());
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
        _trial.Last.Finish(TenPercent);

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
        var estimated = _trial.Last.Create;
        Assert.AreEqual(Path.Combine(_trip, "07 - Viagem Bahia.arkpar-novo.par2"), create.RecoveryFile, "Written under a draft name, given the set's name once made.");
        Assert.AreEqual((estimated.BaseDirectory, estimated.RedundancyPercent, estimated.Options), (create.BaseDirectory, create.RedundancyPercent, create.Options), "The create is the very one estimated.");
        CollectionAssert.AreEqual(estimated.SourceFiles.ToArray(), create.SourceFiles.ToArray());
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

        _engine.Last.WriteSet(indexSize: 5648, recoveryFileSize: 629568, recoveryFileEnd: ".vol0+24.par2");
        _engine.Last.Finish(new EngineResult(EngineOutcome.Completed, 0, null, "Created successfully"));
        await creating;

        Assert.AreEqual(CreateStage.Created, _screen.Stage);
        Assert.AreEqual("Conjunto criado", _screen.Heading);
        Assert.AreEqual(new Banner(BannerKind.Success, "Os arquivos de recuperação protegem 2 arquivos · 5,7 MB.", $"2 arquivos · 620 KB, em {_trip}"), _screen.Banner);
        Assert.IsTrue(_engine.Last.Disposed);
        CollectionAssert.AreEquivalent(
            new[] { RecoveryFile, Path.Combine(_trip, "07 - Viagem Bahia.vol0+24.par2") },
            Directory.GetFiles(_trip, "*.par2"),
            "The draft took the set's name.");

        await WaitUntilAsync(() => !_screen.IsReviewing);
        Assert.AreEqual("Pular · em dia", _screen.Items.Single().StatusText, "Creating again has nothing to do.");
        Assert.IsFalse(_screen.CanCreate);
    }

    [TestMethod]
    public async Task ASetOfTheSameNameGoesToTheRecycleBinOnlyOnceTheNewOneIsMade()
    {
        var (index, recoveryFile) = WriteSetOfTheSameName();
        await _screen.AddAsync([_trip]);
        _trial.Last.Finish(TenPercent);
        await WaitUntilAsync(() => !_screen.IsReviewing);

        Assert.AreEqual("Pular · em dia", _screen.Items.Single().StatusText);
        Assert.IsFalse(_screen.CanCreate, "Nothing to create.");
        Assert.AreEqual("Nenhum conjunto será criado", _screen.SetsTitle);

        _screen.Items.Single().IsIncluded = true;

        Assert.AreEqual("Refazer · em dia", _screen.Items.Single().StatusText);
        Assert.IsTrue(_screen.CanCreate);
        Assert.AreEqual("Substituir arquivos de recuperação", _screen.CreateText);
        Assert.AreEqual("Já existe um conjunto com este nome nesta pasta. Ele só sai da pasta depois que o novo ficar pronto, e vai para a Lixeira.", _screen.ReplaceWarning);
        Assert.HasCount(2, _trial.Last.Create.SourceFiles, "The replaced set's files are not protected by the new one.");

        var creating = _screen.CreateAsync();
        await WaitUntilAsync(() => _screen.CanControl);

        Assert.IsEmpty(_recycled, "Not while the engine works.");
        Assert.IsTrue(File.Exists(index) && File.Exists(recoveryFile));
        _engine.Last.WriteSet(indexSize: 20);
        _engine.Last.Finish(new EngineResult(EngineOutcome.Completed, 0, null, "Created successfully"));
        await creating;

        Assert.AreEqual(CreateStage.Created, _screen.Stage, _screen.Details);
        CollectionAssert.AreEqual(new[] { index, recoveryFile }, _recycled);
        CollectionAssert.AreEquivalent(new[] { index, Path.Combine(_trip, "07 - Viagem Bahia.vol0+10.par2") }, Directory.GetFiles(_trip, "*.par2"));
        Assert.HasCount(20, File.ReadAllBytes(index), "The new set.");
    }

    [TestMethod]
    public async Task CanceledCreateLeavesNoRecoveryFiles()
    {
        await ReadyAsync();
        var creating = _screen.CreateAsync();

        // Left behind by an engine ended before it could clean up (cancel grace period over).
        _engine.Last.WriteSet(recoveryFileEnd: ".vol0+24.par2");
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
    public async Task WhileTheReplacedSetGoesToTheRecycleBinThereIsNoEngineToPause()
    {
        WriteSetOfTheSameName();
        using var recycling = new ManualResetEventSlim();
        using var screen = NewScreen(files =>
        {
            recycling.Wait(TimeSpan.FromSeconds(10));
            Recycle(files);
        });
        await ReadyAsync(screen);
        var creating = screen.CreateAsync();
        await WaitUntilAsync(() => screen.CanControl);

        _engine.Last.WriteSet();
        _engine.Last.Finish(new EngineResult(EngineOutcome.Completed, 0, null, "Created successfully"));
        await WaitUntilAsync(() => screen.ActivityText == "Levando o conjunto anterior para a Lixeira");

        Assert.IsFalse(screen.CanControl);
        recycling.Set();
        await creating;
        Assert.AreEqual(CreateStage.Created, screen.Stage, screen.Details);
    }

    [TestMethod]
    public async Task ClosingWhileTheReplacedSetGoesToTheRecycleBinLeavesNoEngineRunning()
    {
        WriteSetOfTheSameName();
        using var recycling = new ManualResetEventSlim();
        var screen = NewScreen(files =>
        {
            recycling.Wait(TimeSpan.FromSeconds(10));
            Recycle(files);
        });
        await ReadyAsync(screen);
        var creating = screen.CreateAsync();
        await WaitUntilAsync(() => screen.CanControl);
        _engine.Last.WriteSet();
        _engine.Last.Finish(new EngineResult(EngineOutcome.Completed, 0, null, "Created successfully"));
        await WaitUntilAsync(() => screen.ActivityText == "Levando o conjunto anterior para a Lixeira");

        screen.Dispose();
        recycling.Set();
        await creating;

        Assert.HasCount(1, _engine.Runs);
        Assert.IsTrue(_engine.Last.Disposed, "No engine left running after the window closed.");
    }

    [TestMethod]
    public async Task APartialFileThatCannotBeDeletedIsReportedNotThrown()
    {
        await ReadyAsync();
        var creating = _screen.CreateAsync();
        var partial = Path.Combine(_trip, "07 - Viagem Bahia.arkpar-novo.vol0+24.par2");

        // Held by another program, as an antivirus scanning it would.
        using (new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            _engine.Last.Finish(new EngineResult(EngineOutcome.Canceled, 2, null, "Cancel"));
            await creating;
        }

        Assert.AreEqual(CreateStage.Canceled, _screen.Stage);
        StringAssert.Contains(_screen.Banner?.Detail, "apague os que começam com \"07 - Viagem Bahia.arkpar-novo\"");
        Assert.IsNotNull(_screen.Details);
    }

    [TestMethod]
    public async Task ClosingWhileAPartialFileIsHeldClosesWithoutAnErrorAndLeavesTheSetThere()
    {
        var (index, recoveryFile) = WriteSetOfTheSameName();
        await ReadyAsync();
        var creating = _screen.CreateAsync();
        await WaitUntilAsync(() => _screen.CanControl);
        _engine.Last.WriteSet();
        var partial = Path.Combine(_trip, "07 - Viagem Bahia.arkpar-novo.vol0+10.par2");

        // Held by another program, as an antivirus scanning it would, while the window closes.
        using (new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var closing = Task.Run(_screen.Dispose);

            // The killed engine ends as canceled, as the real one does.
            await WaitUntilAsync(() => _engine.Last.Disposed);
            _engine.Last.Finish(new EngineResult(EngineOutcome.Canceled, 1, null, ""));
            await closing;
            await creating;
        }

        Assert.IsEmpty(_recycled);
        Assert.HasCount(10, File.ReadAllBytes(index), "The set there is untouched.");
        Assert.HasCount(10, File.ReadAllBytes(recoveryFile), "The set there is untouched.");
        Assert.IsTrue(File.Exists(partial), "The held partial file stays, without a warning.");

        // Not lost: the next time, creating this set stops on what was left and names it.
        using var reopened = NewScreen(Recycle);
        await ReadyAsync(reopened);
        await reopened.CreateAsync();

        Assert.HasCount(1, _engine.Runs, "Nothing written over it.");
        Assert.AreEqual("Já existe na pasta um conjunto \"07 - Viagem Bahia.arkpar-novo.par2\", deixado por uma criação anterior.", reopened.Banner?.Title);
    }

    [TestMethod]
    public async Task AFailedCreateLeavesTheSetThereAsItWas()
    {
        var (index, recoveryFile) = WriteSetOfTheSameName();
        await ReadyAsync();
        var creating = _screen.CreateAsync();
        await WaitUntilAsync(() => _screen.CanControl);

        _engine.Last.WriteSet(indexSize: 20);
        _engine.Last.Fail(new InvalidOperationException("The engine's output could not be read."));
        await creating;

        Assert.AreEqual(CreateStage.Failed, _screen.Stage);
        StringAssert.Contains(_screen.Banner?.Detail, "O conjunto anterior continua na pasta.");
        Assert.IsEmpty(_recycled);
        CollectionAssert.AreEquivalent(new[] { index, recoveryFile }, Directory.GetFiles(_trip, "*.par2"), "The draft is gone; the set there is untouched.");
        Assert.HasCount(10, File.ReadAllBytes(index));
        await WaitUntilAsync(() => !_screen.IsReviewing);
        Assert.IsTrue(_screen.Items.Single().Comparison.Replaces, "Creating again would still replace it.");
    }

    [TestMethod]
    public async Task ACanceledCreateLeavesTheSetThereAsItWas()
    {
        var (index, recoveryFile) = WriteSetOfTheSameName();
        await ReadyAsync();
        var creating = _screen.CreateAsync();
        await WaitUntilAsync(() => _screen.CanControl);

        _engine.Last.WriteSet(indexSize: 20, recoveryFileSize: 20, recoveryFileEnd: ".vol0+24.par2");
        _engine.Last.Finish(new EngineResult(EngineOutcome.Canceled, 2, null, "Cancel"));
        await creating;

        CollectionAssert.AreEquivalent(new[] { index, recoveryFile }, Directory.GetFiles(_trip, "*.par2"));
        Assert.HasCount(10, File.ReadAllBytes(index), "The set from before, not the half-written one.");
        Assert.AreEqual(new Banner(BannerKind.Warning, "Nenhum arquivo de recuperação foi gravado.", "O conjunto anterior continua na pasta."), _screen.Banner);
        Assert.IsEmpty(_recycled);
    }

    [TestMethod]
    public async Task WithoutARecycleBinTheReplacedSetIsDeletedOnlyOnceTheNewOneIsMade()
    {
        var (index, recoveryFile) = WriteSetOfTheSameName();
        using var screen = NewScreen(Recycle, hasRecycleBin: false);
        await ReadyAsync(screen);

        Assert.AreEqual("Já existe um conjunto com este nome nesta pasta. Ele só sai da pasta depois que o novo ficar pronto, e é apagado, porque este disco não tem Lixeira.", screen.ReplaceWarning);
        var creating = screen.CreateAsync();
        await WaitUntilAsync(() => screen.CanControl);

        Assert.IsTrue(File.Exists(index) && File.Exists(recoveryFile), "Untouched while the engine works.");
        _engine.Last.WriteSet(indexSize: 20);
        _engine.Last.Finish(new EngineResult(EngineOutcome.Completed, 0, null, "Created successfully"));
        await creating;

        Assert.AreEqual(CreateStage.Created, screen.Stage, screen.Details);
        Assert.IsEmpty(_recycled);
        CollectionAssert.AreEquivalent(new[] { index, Path.Combine(_trip, "07 - Viagem Bahia.vol0+10.par2") }, Directory.GetFiles(_trip, "*.par2"), "Only the new set is left.");
        Assert.HasCount(20, File.ReadAllBytes(index));
    }

    [TestMethod]
    public async Task ASetThereThatCannotBeRemovedKeepsTheNewOneUnderItsDraftName()
    {
        var (index, _) = WriteSetOfTheSameName();
        using var screen = NewScreen(files => throw new IOException("O arquivo está aberto em outro programa."));
        await ReadyAsync(screen);
        var creating = screen.CreateAsync();
        await WaitUntilAsync(() => screen.CanControl);

        _engine.Last.WriteSet(indexSize: 20);
        _engine.Last.Finish(new EngineResult(EngineOutcome.Completed, 0, null, "Created successfully"));
        await creating;

        Assert.AreEqual(CreateStage.Failed, screen.Stage);
        Assert.AreEqual(
            new Banner(BannerKind.Error, "Não foi possível tirar o conjunto anterior da pasta.", "Confira se algum dos arquivos dele está aberto em outro programa. O novo conjunto ficou na pasta como \"07 - Viagem Bahia.arkpar-novo.par2\"."),
            screen.Banner);
        Assert.HasCount(10, File.ReadAllBytes(index), "The set there stays.");
        Assert.HasCount(20, File.ReadAllBytes(Path.Combine(_trip, "07 - Viagem Bahia.arkpar-novo.par2")), "The new set is whole, so it stays too.");
    }

    [TestMethod]
    public async Task ADraftLeftByAnEarlierCreateIsNeverRemoved()
    {
        // The set it replaced went, but the rename failed: the draft may be the only set of these photos.
        var draft = Path.Combine(_trip, "07 - Viagem Bahia.arkpar-novo.par2");
        File.WriteAllBytes(draft, new byte[30]);
        await ReadyAsync();

        await _screen.CreateAsync();

        Assert.AreEqual(CreateStage.Failed, _screen.Stage);
        Assert.IsEmpty(_engine.Runs, "Nothing written over it.");
        Assert.HasCount(30, File.ReadAllBytes(draft));
        Assert.AreEqual("Já existe na pasta um conjunto \"07 - Viagem Bahia.arkpar-novo.par2\", deixado por uma criação anterior.", _screen.Banner?.Title);
        StringAssert.Contains(_screen.Banner?.Detail, "confira-o em Verificar e reparar");
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
        }, _listing.ListAsync, Recycle);
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

    /// <summary>A set "07 - Viagem Bahia" made before, of the two photos, 10 bytes per file.</summary>
    private (string Index, string RecoveryFile) WriteSetOfTheSameName()
    {
        var index = Path.Combine(_trip, "07 - Viagem Bahia.par2");
        var recoveryFile = Path.Combine(_trip, "07 - Viagem Bahia.vol0+19.par2");
        File.WriteAllBytes(index, new byte[10]);
        File.WriteAllBytes(recoveryFile, new byte[10]);
        return (index, recoveryFile);
    }

    private CreateScreen NewScreen(Action<IReadOnlyList<string>> recycle, bool hasRecycleBin = true) =>
        new(_engine.Start, _trial.RunAsync, _listing.ListAsync, recycle, _ => hasRecycleBin);

    /// <summary>What the Recycle Bin does: the files leave the folder.</summary>
    private void Recycle(IReadOnlyList<string> files)
    {
        _recycled.AddRange(files);
        Array.ForEach([.. files], File.Delete);
    }

    private Task ReadyAsync() => ReadyAsync(_screen);

    /// <summary>The photos chosen and estimated, and the set included even if it is up to date.</summary>
    private async Task ReadyAsync(CreateScreen screen)
    {
        await screen.AddAsync([_trip]);
        _trial.Last.Finish(TenPercent);
        await WaitUntilAsync(() => !screen.IsReviewing);
        screen.Items.Single().IsIncluded = true;
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
