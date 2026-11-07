// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;
using ArkPar.Screens;

namespace ArkPar.Tests.Screens;

/// <summary>
/// Verifying a folder from the Verify and repair screen, driven as the user would, over results captured from the
/// real engine. "P:\Fotos\2019" holds a set of its own, one in Natal, and one per photo in Praia (made per file).
/// </summary>
[TestClass]
public sealed class FolderVerifyScreenTests : IDisposable
{
    private const string Root = @"P:\Fotos\2019";
    private static readonly FoundSet RootSet = new(@"P:\Fotos\2019\2019.par2", Root, "2019");
    private static readonly FoundSet Natal = new(@"P:\Fotos\2019\Natal\Natal.par2", @"P:\Fotos\2019\Natal", "Natal");
    private static readonly FoundSet Photo1 = new(@"P:\Fotos\2019\Praia\IMG_1.JPG.par2", @"P:\Fotos\2019\Praia", "IMG_1.JPG");
    private static readonly FoundSet Photo2 = new(@"P:\Fotos\2019\Praia\IMG_2.JPG.par2", @"P:\Fotos\2019\Praia", "IMG_2.JPG");
    private static readonly FoundSet Photo3 = new(@"P:\Fotos\2019\Praia\IMG_3.JPG.par2", @"P:\Fotos\2019\Praia", "IMG_3.JPG");
    private static readonly FoundSet[] AllSets = [RootSet, Natal, Photo1, Photo2, Photo3];
    private static readonly string[] FolderNames = ["2019", @"2019\Natal", @"2019\Praia"];
    private static readonly string[] FolderSets = ["2019.par2", "Natal.par2", "3 conjuntos"];
    private static readonly string[] LeftOutFolderNames = ["2019", @"2019\Atalho", @"2019\Natal", @"2019\Privado"];
    private static readonly string[] PraiaFiles = ["IMG_1.JPG.par2", "IMG_2.JPG.par2", "IMG_3.JPG.par2"];

    private static readonly EngineResult Complete = FakeEngine.Result(EngineOutcome.Completed, 0, "verify-complete");
    private static readonly EngineResult Damaged = FakeEngine.Result(EngineOutcome.RepairPossible, 164, "verify-damaged");
    private static readonly EngineResult Impossible = FakeEngine.Result(EngineOutcome.RepairImpossible, 12, "verify-impossible");
    private static readonly EngineResult Repaired = FakeEngine.Result(EngineOutcome.Completed, 16, "repair");
    private static readonly EngineResult Unreadable = new(EngineOutcome.Error, 1, Report: null, "Valid file is not found");
    private static readonly EngineResult Canceled = new(EngineOutcome.Canceled, 2, Report: null, "Cancel");

    private readonly FakeEngine _engine = new();
    private readonly VerifyScreen _detail;
    private readonly FolderVerifyScreen _screen;
    private FoundSets _found = new(AllSets, []);

    public FolderVerifyScreenTests()
    {
        _detail = new VerifyScreen(_engine.Start, lastWritten: _ => null);
        _screen = new FolderVerifyScreen(_engine.Start, (_, _) => _found);
        _screen.Follow(_detail);
    }

    public void Dispose()
    {
        _screen.Dispose();
        _detail.Dispose();
    }

    [TestMethod]
    public async Task ChoosingAFolderListsEverySetBeforeVerifying()
    {
        await _screen.ChooseAsync(Root);

        Assert.AreEqual(FolderVerifyStage.Found, _screen.Stage);
        Assert.IsTrue(_screen.IsActive);
        Assert.IsEmpty(_engine.Runs, "Nothing is verified before the user starts.");
        Assert.AreEqual("5 conjuntos em 3 pastas", _screen.FoundText);
        CollectionAssert.AreEqual(FolderNames, _screen.Folders.Select(folder => folder.Name).ToArray());
        CollectionAssert.AreEqual(FolderSets, _screen.Folders.Select(folder => folder.SetsText).ToArray());
        Assert.AreEqual("Aguardando", _screen.Folders[0].HealthText);

        var praia = _screen.Folders[2];
        Assert.IsTrue(praia.HasSetList, "A folder with several sets opens into them before they are verified too.");
        Assert.AreEqual("Ver os conjuntos", praia.SetListText);
        CollectionAssert.AreEqual(PraiaFiles, praia.ListedSets.Select(set => set.FileName).ToArray());
        Assert.IsTrue(_screen.CanStart);
        Assert.IsNull(_screen.Banner);
    }

    [TestMethod]
    public async Task FolderWithoutSetsSaysSo()
    {
        _found = new FoundSets([], []);

        await _screen.ChooseAsync(Root);

        Assert.IsFalse(_screen.CanStart);
        Assert.AreEqual(new Banner(BannerKind.Warning, "Nenhum conjunto de recuperação foi encontrado nesta pasta.", "Os conjuntos são os arquivos .par2 feitos na tela Criar."), _screen.Banner);
    }

    [TestMethod]
    public async Task FoldersLeftOutAreListedAsNotVerifiedInTheOrderMet()
    {
        _found = new FoundSets([RootSet, Natal], [new LeftOutFolder(@"P:\Fotos\2019\Atalho", LeftOutReason.Link), new LeftOutFolder(@"P:\Fotos\2019\Privado", LeftOutReason.Unreadable)]);

        await _screen.ChooseAsync(Root);

        Assert.IsTrue(_screen.CanStart);
        Assert.AreEqual("2 conjuntos em 2 pastas", _screen.FoundText);
        CollectionAssert.AreEqual(LeftOutFolderNames, _screen.Folders.Select(folder => folder.Name).ToArray());
        var (link, unreadable) = (_screen.Folders[1], _screen.Folders[3]);
        Assert.AreEqual((HealthGroup?)HealthGroup.Unverified, link.Group);
        Assert.AreEqual("Atalho para outra pasta, não aberto · nada nele foi verificado", link.HealthText);
        Assert.AreEqual((HealthGroup?)HealthGroup.Unverified, unreadable.Group);
        Assert.AreEqual("Não pôde ser lida · nada nela foi verificado", unreadable.HealthText);
        Assert.AreEqual("", unreadable.SetsText);
        Assert.IsFalse(unreadable.HasSetList);
        Assert.AreEqual(new Banner(BannerKind.Warning, "2 pastas ficaram de fora.", "Elas aparecem na lista como não verificadas."), _screen.Banner);
        Assert.Contains(@"P:\Fotos\2019\Privado", _screen.Details ?? "");
    }

    [TestMethod]
    public async Task SummaryNeverSaysAllIntactWhileAFolderWasLeftOut()
    {
        _found = new FoundSets([Natal], [new LeftOutFolder(@"P:\Fotos\2019\Privado", LeftOutReason.Unreadable)]);

        await VerifyAllAsync(Complete);

        Assert.AreEqual(new FolderCounts(Complete: 1, Repairable: 0, Unrepairable: 0, Unverified: 1), _screen.Counts);
        Assert.AreEqual(new Banner(BannerKind.Warning, "2 pastas: 1 íntegra e 1 não verificada.", "1 pasta ficou de fora da verificação: o motivo está na lista."), _screen.Banner);
        Assert.Contains(@"P:\Fotos\2019\Privado", _screen.Details ?? "");
    }

    [TestMethod]
    public async Task NoSetInTheFoldersReadButSomeLeftOutDoesNotSayThereIsNone()
    {
        _found = new FoundSets([], [new LeftOutFolder(@"P:\Fotos\2019\Privado", LeftOutReason.Unreadable)]);

        await _screen.ChooseAsync(Root);

        Assert.IsFalse(_screen.CanStart);
        Assert.AreEqual(new Banner(BannerKind.Warning, "Nenhum conjunto foi encontrado nas pastas que puderam ser abertas.", "1 pasta ficou de fora e aparece na lista como não verificada."), _screen.Banner);
        Assert.AreEqual("Não pôde ser lida · nada nela foi verificado", _screen.Folders.Single().HealthText);
    }

    [TestMethod]
    public async Task ChosenFolderThatCannotBeReadSaysSoRatherThanEmpty()
    {
        _found = new FoundSets([], [new LeftOutFolder(Root, LeftOutReason.Unreadable)]);

        await _screen.ChooseAsync(Root);

        Assert.IsFalse(_screen.CanStart);
        Assert.AreEqual(new Banner(BannerKind.Error, "A pasta não pôde ser lida.", "Confira se ela existe e se o disco está acessível."), _screen.Banner);
    }

    [TestMethod]
    public async Task SearchCanBeCanceledAndGoesBackToASingleSet()
    {
        using var screen = new FolderVerifyScreen(_engine.Start, (_, stop) =>
        {
            stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            stop.ThrowIfCancellationRequested();
            return _found;
        });

        var choosing = screen.ChooseAsync(Root);
        Assert.AreEqual(FolderVerifyStage.Searching, screen.Stage);
        Assert.IsTrue(screen.CanCancel);
        Assert.IsFalse(screen.CanPause, "A search cannot be paused.");

        screen.Cancel();
        await choosing;

        Assert.AreEqual(FolderVerifyStage.Idle, screen.Stage);
        Assert.IsEmpty(screen.Folders);
    }

    [TestMethod]
    public async Task SetsAreVerifiedOneAfterAnotherSayingWhichOfHowMany()
    {
        await _screen.ChooseAsync(Root);

        var verifying = _screen.StartAsync();

        Assert.AreEqual(FolderVerifyStage.Verifying, _screen.Stage);
        Assert.IsTrue(_screen.IsRunning);
        Assert.HasCount(1, _engine.Runs, "One set at a time.");
        Assert.AreEqual(new EngineCommand.Verify(RootSet.RecoveryFile), _engine.Last.Command);
        Assert.AreEqual(@"Conjunto 1 de 5: 2019\2019.par2", _screen.ProgressText);
        Assert.AreEqual("Verificando…", _screen.Folders[0].HealthText);

        _engine.Last.Report("praia 01.jpg", 40);
        Assert.AreEqual("Lendo praia 01.jpg", _screen.ActivityText);
        Assert.AreEqual(40, _screen.Percent);

        _engine.Last.Finish(Complete);
        await Task.Yield();

        Assert.HasCount(2, _engine.Runs);
        Assert.AreEqual(new EngineCommand.Verify(Natal.RecoveryFile), _engine.Last.Command);
        Assert.AreEqual(@"Conjunto 2 de 5: 2019\Natal\Natal.par2", _screen.ProgressText);
        Assert.IsNull(_screen.Percent, "A new set starts with no progress.");
        Assert.AreEqual("3 arquivos, todos íntegros", _screen.Folders[0].HealthText);

        await FinishEachAsync(verifying, Damaged, Complete, Complete, Complete);
        Assert.AreEqual(FolderVerifyStage.Done, _screen.Stage);
        Assert.IsNull(_screen.ProgressText);
    }

    [TestMethod]
    public async Task SummarySortsTheFoldersByWhatTheyNeed()
    {
        await VerifyAllAsync(Complete, Damaged, Complete, Impossible, Complete);

        Assert.AreEqual("Verificação concluída", _screen.Heading);
        Assert.AreEqual(new FolderCounts(Complete: 1, Repairable: 1, Unrepairable: 1, Unverified: 0), _screen.Counts);
        Assert.AreEqual(new Banner(BannerKind.Error, "3 pastas: 1 íntegra, 1 com problema reparável e 1 com problema sem reparo.", Detail: null), _screen.Banner);

        var (root, natal, praia) = (_screen.Folders[0], _screen.Folders[1], _screen.Folders[2]);
        Assert.AreEqual((HealthGroup?)HealthGroup.Complete, root.Group);
        Assert.AreEqual((HealthGroup?)HealthGroup.Repairable, natal.Group);
        Assert.AreEqual("3 arquivos com problema · o reparo é possível", natal.HealthText);
        Assert.AreEqual((HealthGroup?)HealthGroup.Unrepairable, praia.Group, "A folder is as bad as its worst set.");
        Assert.AreEqual("2 íntegros · 1 com problema sem reparo", praia.HealthText);
        Assert.AreEqual("2 arquivos com problema · o reparo não é possível", praia.ListedSets.Single().HealthText);
        Assert.AreEqual("IMG_2.JPG.par2", praia.ListedSets.Single().FileName);
        Assert.AreEqual("Ver os conjuntos com problema", praia.SetListText);
        Assert.IsTrue(_screen.CanRepairAll);
    }

    [TestMethod]
    public async Task EverythingIntactSaysSo()
    {
        await VerifyAllAsync(Complete, Complete, Complete, Complete, Complete);

        Assert.AreEqual(new Banner(BannerKind.Success, "As 3 pastas estão íntegras.", Detail: null), _screen.Banner);
        Assert.IsFalse(_screen.CanRepairAll);
    }

    [TestMethod]
    public async Task UnreadableRecoveryFileIsNotVerifiedRatherThanLost()
    {
        _found = new FoundSets([Natal], []);

        await VerifyAllAsync(Unreadable);

        Assert.AreEqual(new FolderCounts(Complete: 0, Repairable: 0, Unrepairable: 0, Unverified: 1), _screen.Counts);
        Assert.AreEqual(new Banner(BannerKind.Warning, "1 pasta: 1 não verificada.", Detail: null), _screen.Banner);
        Assert.AreEqual("O arquivo de recuperação não pôde ser lido", _screen.Folders.Single().HealthText);
    }

    [TestMethod]
    public async Task EngineThatCannotStartLeavesThatSetUnverifiedAndGoesOn()
    {
        _found = new FoundSets([Natal, Photo1], []);
        var runs = 0;
        using var screen = new FolderVerifyScreen(
            (command, progress) => ++runs == 1 ? throw new FileNotFoundException("The engine (par2j64.exe) was not found.") : _engine.Start(command, progress),
            (_, _) => _found);
        await screen.ChooseAsync(Root);

        var verifying = screen.StartAsync();
        _engine.Last.Finish(Complete);
        await verifying;

        Assert.AreEqual(SetHealth.Unreadable, screen.Folders[0].Sets[0].Health);
        Assert.AreEqual(SetHealth.Complete, screen.Folders[1].Sets[0].Health);
        Assert.Contains("par2j64.exe", screen.Details ?? "");
    }

    [TestMethod]
    public async Task CancelKeepsWhatWasVerifiedAndLeavesTheRestUnverified()
    {
        await _screen.ChooseAsync(Root);
        var verifying = _screen.StartAsync();
        _engine.Last.Finish(Complete);
        await Task.Yield();

        _screen.Cancel();
        Assert.IsTrue(_engine.Last.CancelRequested);
        _engine.Last.Finish(Canceled);
        await verifying;

        Assert.HasCount(2, _engine.Runs, "The sets after the canceled one never start.");
        Assert.AreEqual(FolderVerifyStage.Done, _screen.Stage);
        Assert.AreEqual("Verificação cancelada", _screen.Heading);
        Assert.AreEqual(SetHealth.Complete, _screen.Folders[0].Sets[0].Health);
        Assert.AreEqual(new FolderCounts(Complete: 1, Repairable: 0, Unrepairable: 0, Unverified: 2), _screen.Counts);
        Assert.AreEqual(
            new Banner(BannerKind.Warning, "3 pastas: 1 íntegra e 2 não verificadas.", "4 conjuntos ficaram sem verificar, ou com o reparo interrompido. Verifique a pasta de novo para saber como estão."),
            _screen.Banner);
    }

    [TestMethod]
    public async Task PauseAndContinueTheSetUnderWay()
    {
        await _screen.ChooseAsync(Root);
        _ = _screen.StartAsync();

        _screen.TogglePause();
        Assert.IsTrue(_engine.Last.IsPaused);
        Assert.AreEqual("Continuar", _screen.PauseText);

        _screen.TogglePause();
        Assert.IsFalse(_engine.Last.IsPaused);
    }

    [TestMethod]
    public async Task RepairAllRepairsOnlyTheRepairableSetsInTurn()
    {
        await VerifyAllAsync(Complete, Damaged, Damaged, Impossible, Complete);
        var verifications = _engine.Runs.Count;

        var repairing = _screen.RepairAllAsync();

        Assert.AreEqual(FolderVerifyStage.Repairing, _screen.Stage);
        Assert.AreEqual(@"Conjunto 1 de 2: 2019\Natal\Natal.par2", _screen.ProgressText);
        Assert.AreEqual("Reparando…", _screen.Folders[1].HealthText);
        await FinishEachAsync(repairing, Repaired, Repaired);

        CollectionAssert.AreEqual(
            new EngineCommand[] { new EngineCommand.Repair(Natal.RecoveryFile), new EngineCommand.Repair(Photo1.RecoveryFile) },
            _engine.Runs.Skip(verifications).Select(run => run.Command).ToArray());
        Assert.AreEqual("Reparo concluído", _screen.Heading);
        Assert.AreEqual("3 arquivos consertados", _screen.Folders[1].HealthText);
        Assert.AreEqual(new FolderCounts(Complete: 2, Repairable: 0, Unrepairable: 1, Unverified: 0), _screen.Counts);
        Assert.AreEqual(new Banner(BannerKind.Error, "3 pastas: 2 íntegras e 1 com problema sem reparo.", Detail: null), _screen.Banner);
        Assert.IsFalse(_screen.CanRepairAll);
    }

    [TestMethod]
    public async Task RepairThatFailsCountsAsWithoutRepair()
    {
        _found = new FoundSets([Natal], []);
        await VerifyAllAsync(Damaged);

        var repairing = _screen.RepairAllAsync();
        await FinishEachAsync(repairing, FakeEngine.Result(EngineOutcome.RepairImpossible, 20, "repair"));

        Assert.AreEqual(SetHealth.RepairFailed, _screen.Folders.Single().Sets.Single().Health);
        Assert.AreEqual(new FolderCounts(Complete: 0, Repairable: 0, Unrepairable: 1, Unverified: 0), _screen.Counts);
    }

    [TestMethod]
    public async Task CanceledRepairAllLeavesTheSetUnderWayUnverifiedAndTheRestRepairable()
    {
        _found = new FoundSets([Natal, Photo1], []);
        await VerifyAllAsync(Damaged, Damaged);

        var repairing = _screen.RepairAllAsync();
        _screen.Cancel();
        _engine.Last.Finish(Canceled);
        await repairing;

        Assert.AreEqual("Reparo cancelado", _screen.Heading);
        Assert.AreEqual(SetHealth.NotVerified, _screen.Folders[0].Sets[0].Health, "The engine may have fixed some files before it stopped.");
        Assert.AreEqual(SetHealth.Repairable, _screen.Folders[1].Sets[0].Health);
        Assert.IsTrue(_screen.CanRepairAll);
    }

    [TestMethod]
    public async Task DetailShowsTheVerificationFoundWithoutRunningTheEngineAgain()
    {
        await VerifyAllAsync(Complete, Damaged, Complete, Complete, Complete);
        var runs = _engine.Runs.Count;
        var natal = _screen.Folders[1].Sets.Single();

        await _screen.OpenAsync(natal);

        Assert.HasCount(runs, _engine.Runs);
        Assert.AreSame(natal, _screen.OpenedSet);
        Assert.IsFalse(_screen.IsSummaryShown);
        Assert.AreEqual(Natal.RecoveryFile, _detail.RecoveryFile);
        Assert.HasCount(3, _detail.ProblemFiles);
        Assert.IsTrue(_detail.CanRepair);

        _screen.CloseDetail();

        Assert.IsNull(_screen.OpenedSet);
        Assert.IsTrue(_screen.IsSummaryShown);
    }

    [TestMethod]
    public async Task RepairInTheDetailUpdatesTheSummary()
    {
        _found = new FoundSets([Natal, Photo1], []);
        await VerifyAllAsync(Damaged, Complete);
        await _screen.OpenAsync(_screen.Folders[0].Sets[0]);

        var repairing = _detail.RepairAsync();

        Assert.IsFalse(_screen.CanRepairAll, "Nothing starts here while the detail repairs.");
        Assert.IsFalse(_screen.CanOpen);
        _engine.Last.Finish(Repaired);
        await repairing;

        Assert.AreEqual(SetHealth.Repaired, _screen.Folders[0].Sets[0].Health);
        Assert.AreEqual(new Banner(BannerKind.Success, "As 2 pastas estão íntegras.", Detail: null), _screen.Banner);
        Assert.IsTrue(_screen.CanOpen);
    }

    [TestMethod]
    public async Task CanceledVerificationInTheDetailKeepsWhatTheFolderVerificationFound()
    {
        _found = new FoundSets([Natal], []);
        await VerifyAllAsync(Damaged);
        await _screen.OpenAsync(_screen.Folders[0].Sets[0]);

        var verifying = _detail.VerifyAsync(Natal.RecoveryFile);
        _detail.Cancel();
        _engine.Last.Finish(Canceled);
        await verifying;

        Assert.AreEqual(SetHealth.Repairable, _screen.Folders[0].Sets[0].Health, "A verification changes no file: the one before still holds.");
        Assert.IsTrue(_screen.CanRepairAll);
    }

    [TestMethod]
    public async Task RepairInTheDetailThatCannotStartIsShownInTheSummary()
    {
        _found = new FoundSets([Natal], []);
        using var detail = new VerifyScreen(
            (command, progress) => command is EngineCommand.Repair ? throw new FileNotFoundException("The engine (par2j64.exe) was not found.") : _engine.Start(command, progress),
            lastWritten: _ => null);
        using var screen = new FolderVerifyScreen(_engine.Start, (_, _) => _found);
        screen.Follow(detail);
        await screen.ChooseAsync(Root);
        await FinishEachAsync(screen.StartAsync(), Damaged);
        await screen.OpenAsync(screen.Folders[0].Sets[0]);

        await detail.RepairAsync();

        Assert.AreEqual(SetHealth.Unreadable, screen.Folders[0].Sets[0].Health);
        Assert.IsFalse(screen.CanRepairAll, "Repairing it again would fail the same way.");
    }

    [TestMethod]
    public async Task SetWithoutAVerificationIsVerifiedAgainWhenOpened()
    {
        _found = new FoundSets([Natal], []);
        await VerifyAllAsync(Damaged);
        await FinishEachAsync(_screen.RepairAllAsync(), Repaired);
        var runs = _engine.Runs.Count;

        var opening = _screen.OpenAsync(_screen.Folders[0].Sets[0]);

        Assert.HasCount(runs + 1, _engine.Runs);
        Assert.AreEqual(new EngineCommand.Verify(Natal.RecoveryFile), _engine.Last.Command);
        _engine.Last.Finish(Complete);
        await opening;
        Assert.AreEqual(SetHealth.Complete, _screen.Folders[0].Sets[0].Health);
    }

    [TestMethod]
    public async Task LeavingGoesBackToASingleSet()
    {
        await VerifyAllAsync(Complete, Complete, Complete, Complete, Complete);

        _screen.Leave();

        Assert.AreEqual(FolderVerifyStage.Idle, _screen.Stage);
        Assert.IsFalse(_screen.IsActive);
        Assert.IsEmpty(_screen.Folders);
    }

    [TestMethod]
    public async Task ClosingTheScreenEndsTheEngine()
    {
        await _screen.ChooseAsync(Root);
        _ = _screen.StartAsync();

        _screen.Dispose();

        Assert.IsTrue(_engine.Last.Disposed);
    }

    private async Task VerifyAllAsync(params EngineResult[] results)
    {
        await _screen.ChooseAsync(Root);
        await FinishEachAsync(_screen.StartAsync(), results);
    }

    /// <summary>Ends each engine task the screen starts, in turn, with the next result.</summary>
    private async Task FinishEachAsync(Task running, params EngineResult[] results)
    {
        foreach (var result in results)
        {
            _engine.Last.Finish(result);
            await Task.Yield();
        }

        await running;
    }
}
