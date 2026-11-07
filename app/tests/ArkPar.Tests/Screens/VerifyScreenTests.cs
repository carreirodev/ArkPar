// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;
using ArkPar.Screens;

namespace ArkPar.Tests.Screens;

/// <summary>
/// The Verify and repair screen, driven as the user would, over results captured from the real
/// engine: three 2 MB photos in "P:\Fotos de família" (see Engine/Fixtures/README.md).
/// </summary>
[TestClass]
public sealed class VerifyScreenTests : IDisposable
{
    private const string RecoveryFile = @"P:\Fotos de família\fotos.par2";
    private static readonly DateTime July14 = new(2019, 7, 14, 10, 30, 0);
    private static readonly string[] CompleteSetByName = ["aniversário.jpg", "praia 01.jpg", @"viagem\ônibus.jpg"];

    private readonly FakeEngine _engine = new();
    private readonly VerifyScreen _screen;

    public VerifyScreenTests()
    {
        _screen = new VerifyScreen(_engine.Start, lastWritten: _ => July14);
    }

    public void Dispose() => _screen.Dispose();

    [TestMethod]
    public void ChoosingARecoveryFileStartsVerifying()
    {
        _ = _screen.VerifyAsync(RecoveryFile);

        Assert.AreEqual(new EngineCommand.Verify(RecoveryFile), _engine.Last.Command);
        Assert.AreEqual(VerifyStage.Verifying, _screen.Stage);
        Assert.AreEqual("Verificando", _screen.Heading);
        Assert.AreEqual(RecoveryFile, _screen.RecoveryFile);
        Assert.IsTrue(_screen.IsRunning);
        Assert.IsNull(_screen.Percent, "No progress yet: the bar moves on its own.");
        Assert.IsTrue(_screen.IsPercentUnknown);
        Assert.AreEqual("Lendo o arquivo de recuperação", _screen.ActivityText);
    }

    [TestMethod]
    public void ProgressNamesThePhotoBeingRead()
    {
        _ = _screen.VerifyAsync(RecoveryFile);

        _engine.Last.Report("praia 01.jpg", 42.5);

        Assert.AreEqual(42.5, _screen.Percent);
        Assert.IsFalse(_screen.IsPercentUnknown);
        Assert.AreEqual("Lendo praia 01.jpg", _screen.ActivityText);
    }

    [TestMethod]
    public void ProgressCountsTheFilesChecked()
    {
        _ = _screen.VerifyAsync(RecoveryFile);

        _engine.Last.Report(EngineProgress.CheckingFiles, 25);

        Assert.AreEqual(25, _screen.Percent);
        Assert.AreEqual("Conferindo os arquivos", _screen.ActivityText);
    }

    [TestMethod]
    public async Task DamagedSetCanBeRepairedWithSomeOfTheRecoveryBlocks()
    {
        await VerifyAsync(FakeEngine.Result(EngineOutcome.RepairPossible, 164, "verify-damaged"));

        Assert.AreEqual(VerifyStage.Verified, _screen.Stage);
        Assert.AreEqual("Verificação concluída", _screen.Heading);
        Assert.IsFalse(_screen.IsRunning);
        Assert.AreEqual(new Banner(BannerKind.Warning, "3 arquivos com problema. O reparo é possível.", "Precisa de 81 dos 120 blocos de recuperação disponíveis."), _screen.Banner);
        Assert.IsTrue(_screen.CanRepair);
        Assert.AreEqual(new FileCounts(Complete: 0, Damaged: 1, Missing: 1, Misnamed: 1), _screen.Counts);
    }

    [TestMethod]
    public async Task ProblemsComeFirstWithAnImageOnlyForFilesOnDisk()
    {
        await VerifyAsync(FakeEngine.Result(EngineOutcome.RepairPossible, 164, "verify-damaged"));

        CollectionAssert.AreEqual(
            new[]
            {
                new ProblemFile("praia 01.jpg", ProblemKind.Damaged, "Danificado", @"P:\Fotos de família\praia 01.jpg", "1,9 MB · 14/07/2019"),
                new ProblemFile(@"viagem\ônibus.jpg", ProblemKind.Missing, "Faltando", ImagePath: null, "1,9 MB"),
                new ProblemFile("aniversário.jpg", ProblemKind.Misnamed, "Nome trocado", @"P:\Fotos de família\aniversario renomeado.jpg", "1,9 MB · 14/07/2019"),
            },
            _screen.ProblemFiles.ToArray());
        Assert.IsEmpty(_screen.CompleteFiles);
        Assert.AreEqual("Com problema", _screen.ProblemsHeading);
    }

    [TestMethod]
    public async Task UnrepairableSetSaysHowManyBlocksAreLacking()
    {
        await VerifyAsync(FakeEngine.Result(EngineOutcome.RepairImpossible, 12, "verify-impossible"));

        Assert.AreEqual(new Banner(BannerKind.Error, "2 arquivos com problema. O reparo não é possível.", "Faltam 40 blocos de recuperação: o reparo precisa de 160, e há 120."), _screen.Banner);
        Assert.IsFalse(_screen.CanRepair);
        Assert.AreEqual(new FileCounts(Complete: 1, Damaged: 0, Missing: 2, Misnamed: 0), _screen.Counts);
        CollectionAssert.AreEqual(new[] { new CompleteFile(@"viagem\ônibus.jpg", "1,9 MB", "14/07/2019") }, _screen.CompleteFiles.ToArray());
        Assert.AreEqual("1 arquivo · 1,9 MB", _screen.CompleteSummary);
    }

    [TestMethod]
    public async Task CompleteSetNeedsNothing()
    {
        await VerifyAsync(FakeEngine.Result(EngineOutcome.Completed, 0, "verify-complete"));

        Assert.AreEqual(new Banner(BannerKind.Success, "Os 3 arquivos estão íntegros.", Detail: null), _screen.Banner);
        Assert.IsFalse(_screen.CanRepair);
        Assert.IsEmpty(_screen.ProblemFiles);
        Assert.AreEqual("3 arquivos · 5,7 MB", _screen.CompleteSummary);
        CollectionAssert.AreEqual(
            CompleteSetByName,
            _screen.CompleteFiles.Select(file => file.Name).ToArray(),
            "Sorted by name.");
    }

    [TestMethod]
    public async Task DamagedRecoveryFileIsMentionedEvenWhenThePhotosAreFine()
    {
        var complete = FakeEngine.Result(EngineOutcome.Completed, 0, "verify-complete");

        await VerifyAsync(complete with { ExitCode = 256 });

        Assert.AreEqual("Um dos arquivos de recuperação está danificado ou faltando.", _screen.Banner?.Detail);
    }

    [TestMethod]
    public void PauseAndContinue()
    {
        _ = _screen.VerifyAsync(RecoveryFile);
        Assert.AreEqual("Pausar", _screen.PauseText);

        _screen.TogglePause();
        Assert.IsTrue(_engine.Last.IsPaused);
        Assert.IsTrue(_screen.IsPaused);
        Assert.AreEqual("Continuar", _screen.PauseText);

        _screen.TogglePause();
        Assert.IsFalse(_engine.Last.IsPaused);
        Assert.AreEqual("Pausar", _screen.PauseText);
    }

    [TestMethod]
    public async Task CanceledVerificationSaysSo()
    {
        var verifying = _screen.VerifyAsync(RecoveryFile);

        _screen.Cancel();
        Assert.IsTrue(_engine.Last.CancelRequested);
        _engine.Last.Finish(new EngineResult(EngineOutcome.Canceled, 2, Report: null, "Cancel"));
        await verifying;

        Assert.AreEqual(VerifyStage.Canceled, _screen.Stage);
        Assert.AreEqual("Verificação cancelada", _screen.Heading);
        Assert.IsNull(_screen.Banner);
        Assert.IsFalse(_screen.HasBanner);
        Assert.IsNull(_screen.Counts);
        Assert.IsTrue(_engine.Last.Disposed);
    }

    [TestMethod]
    public void ClosingTheScreenEndsTheEngine()
    {
        _ = _screen.VerifyAsync(RecoveryFile);

        _screen.Dispose();

        Assert.IsTrue(_engine.Last.Disposed);
    }

    [TestMethod]
    public async Task RepairRunsOnTheSameRecoveryFile()
    {
        await VerifyAsync(FakeEngine.Result(EngineOutcome.RepairPossible, 164, "verify-damaged"));

        _ = _screen.RepairAsync();

        Assert.AreEqual(new EngineCommand.Repair(RecoveryFile), _engine.Last.Command);
        Assert.AreEqual(VerifyStage.Repairing, _screen.Stage);
        Assert.AreEqual("Reparando", _screen.Heading);
        Assert.IsFalse(_screen.CanRepair);
        Assert.IsNull(_screen.Banner);
        Assert.AreEqual("Conferindo os arquivos antes de reparar", _screen.ActivityText);

        _engine.Last.Report("Recovering slice", 10);
        Assert.AreEqual("Recriando as partes perdidas", _screen.ActivityText);
    }

    [TestMethod]
    public async Task RepairedSetShowsEveryProblemFixed()
    {
        await VerifyAsync(FakeEngine.Result(EngineOutcome.RepairPossible, 164, "verify-damaged"));
        var repairing = _screen.RepairAsync();

        _engine.Last.Finish(FakeEngine.Result(EngineOutcome.Completed, 16, "repair"));
        await repairing;

        Assert.AreEqual(VerifyStage.Repaired, _screen.Stage);
        Assert.AreEqual("Reparo concluído", _screen.Heading);
        Assert.AreEqual("Reparados", _screen.ProblemsHeading);
        Assert.AreEqual("Já estavam íntegros", _screen.CompleteHeading);
        Assert.AreEqual(new Banner(BannerKind.Success, "3 arquivos consertados.", "A versão danificada foi para a Lixeira."), _screen.Banner);
        Assert.AreEqual(new FileCounts(Complete: 3, Damaged: 0, Missing: 0, Misnamed: 0), _screen.Counts);
        CollectionAssert.AreEqual(
            new[]
            {
                new ProblemFile("praia 01.jpg", ProblemKind.Repaired, "Reparado", @"P:\Fotos de família\praia 01.jpg", "1,9 MB · 14/07/2019"),
                new ProblemFile(@"viagem\ônibus.jpg", ProblemKind.Repaired, "Reparado", @"P:\Fotos de família\viagem\ônibus.jpg", "1,9 MB · 14/07/2019"),
                new ProblemFile("aniversário.jpg", ProblemKind.Repaired, "Reparado", @"P:\Fotos de família\aniversário.jpg", "1,9 MB · 14/07/2019"),
            },
            _screen.ProblemFiles.ToArray());
    }

    [TestMethod]
    public async Task FailedRepairSaysNotEverythingWasFixed()
    {
        await VerifyAsync(FakeEngine.Result(EngineOutcome.RepairPossible, 164, "verify-damaged"));
        var repairing = _screen.RepairAsync();

        _engine.Last.Finish(FakeEngine.Result(EngineOutcome.RepairImpossible, 20, "repair"));
        await repairing;

        Assert.AreEqual(VerifyStage.Failed, _screen.Stage);
        Assert.AreEqual("O reparo falhou", _screen.Heading);
        Assert.AreEqual(new Banner(BannerKind.Error, "Não foi possível consertar todos os arquivos.", "Verifique de novo para ver como os arquivos ficaram."), _screen.Banner);

        // The list from before the repair may no longer be true: some files may have been fixed.
        Assert.IsNull(_screen.Counts);
        Assert.IsEmpty(_screen.ProblemFiles);
        Assert.IsFalse(_screen.CanRepair);
    }

    [TestMethod]
    public async Task CanceledRepairAsksForANewVerification()
    {
        await VerifyAsync(FakeEngine.Result(EngineOutcome.RepairPossible, 164, "verify-damaged"));
        var repairing = _screen.RepairAsync();

        _screen.Cancel();
        _engine.Last.Finish(new EngineResult(EngineOutcome.Canceled, 2, Report: null, "Cancel"));
        await repairing;

        Assert.AreEqual(VerifyStage.Canceled, _screen.Stage);
        Assert.AreEqual("Reparo cancelado", _screen.Heading);
        Assert.AreEqual(new Banner(BannerKind.Warning, "O reparo foi interrompido.", "Verifique de novo para ver como os arquivos ficaram."), _screen.Banner);
        Assert.IsNull(_screen.Counts);
    }

    [TestMethod]
    public async Task RepairThatNeedsNoRecoveryBlocksSaysSo()
    {
        // A partial copy of the missing photo holds all of its parts, so no recovery block is used
        // (verify-found.json, with the counts the engine would print).
        var output = "Recovery Slice found\t: 120\r\nInput File Slice lost\t: 0\r\n";
        var report = VerificationReport.Load(Engine.Fixture.PathOf("verify-found.json"), output);

        await VerifyAsync(new EngineResult(EngineOutcome.RepairPossible, 132, report, output));

        Assert.AreEqual("Nenhum bloco de recuperação é usado.", _screen.Banner?.Detail);
    }

    [TestMethod]
    public async Task ResultThatCannotBeShownEndsTheTaskInsteadOfHanging()
    {
        var verifying = _screen.VerifyAsync(RecoveryFile);

        // RepairPossible always comes with a report; one without it cannot be shown.
        _engine.Last.Finish(new EngineResult(EngineOutcome.RepairPossible, 132, Report: null, ""));
        await verifying;

        Assert.AreEqual(VerifyStage.Failed, _screen.Stage);
        Assert.IsFalse(_screen.IsRunning);
        Assert.AreEqual("O motor terminou, mas o resultado não pôde ser lido.", _screen.Banner?.Title);
    }

    [TestMethod]
    public async Task EngineErrorShowsAPlainMessageAndKeepsTheDetails()
    {
        var verifying = _screen.VerifyAsync(RecoveryFile);

        _engine.Last.Finish(new EngineResult(EngineOutcome.Error, 1, Report: null, "Parchive 2.0 client\r\nValid file is not found\r\n"));
        await verifying;

        Assert.AreEqual(VerifyStage.Failed, _screen.Stage);
        Assert.AreEqual("Não foi possível verificar", _screen.Heading);
        Assert.AreEqual(new Banner(BannerKind.Error, "O arquivo de recuperação não pôde ser lido.", "Confira se ele existe e se o disco está acessível."), _screen.Banner);
        Assert.IsNotNull(_screen.Details);
        Assert.Contains("Valid file is not found", _screen.Details);
    }

    [TestMethod]
    public async Task EngineThatCannotStartIsShownNotThrown()
    {
        using var screen = new VerifyScreen((_, _) => throw new FileNotFoundException("The engine (par2j64.exe) was not found.", "par2j64.exe"));

        await screen.VerifyAsync(RecoveryFile);

        Assert.AreEqual(VerifyStage.Failed, screen.Stage);
        Assert.AreEqual(new Banner(BannerKind.Error, "Não foi possível iniciar a verificação.", "O ArkPar pode estar instalado de forma incompleta."), screen.Banner);
        Assert.IsNotNull(screen.Details);
        Assert.Contains("par2j64.exe", screen.Details);
    }

    [TestMethod]
    public async Task ChangesAreAnnouncedForTheScreenToRedraw()
    {
        var changed = new List<string?>();
        _screen.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await VerifyAsync(FakeEngine.Result(EngineOutcome.RepairPossible, 164, "verify-damaged"));

        CollectionAssert.IsSubsetOf(
            new[] { nameof(VerifyScreen.Stage), nameof(VerifyScreen.Heading), nameof(VerifyScreen.Banner), nameof(VerifyScreen.Counts), nameof(VerifyScreen.ProblemFiles), nameof(VerifyScreen.CanRepair) },
            changed);
    }

    private async Task VerifyAsync(EngineResult result)
    {
        var verifying = _screen.VerifyAsync(RecoveryFile);
        _engine.Last.Finish(result);
        await verifying;
    }
}
