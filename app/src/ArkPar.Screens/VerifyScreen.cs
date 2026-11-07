// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;

namespace ArkPar.Screens;

/// <summary>
/// The Verify and repair screen: verifies a recovery set, shows which files have a problem and,
/// when the set can fix them, repairs them. Use it from the screen's thread; the engine's progress
/// is brought back to that thread.
/// </summary>
public sealed class VerifyScreen : Observable, IDisposable
{
    private readonly Func<EngineCommand, IProgress<EngineProgress>, IEngineRun> _startEngine;
    private readonly Func<string, DateTime?> _lastWritten;
    private IEngineRun? _run;
    private int _runNumber;

    private VerifyStage _stage;
    private string _heading = "Verificar e reparar";
    private string? _recoveryFile;
    private string _activityText = "";
    private double? _percent;
    private bool _isPaused;
    private Banner? _banner;
    private bool _canRepair;
    private FileCounts? _counts;
    private IReadOnlyList<ProblemFile> _problemFiles = [];
    private IReadOnlyList<CompleteFile> _completeFiles = [];
    private string _completeSummary = "";
    private string? _details;

    /// <param name="startEngine">Starts an engine task (<see cref="EngineRun.Start"/> in the app).</param>
    /// <param name="lastWritten">When a file on disk was last changed, or null if it is not there.</param>
    public VerifyScreen(Func<EngineCommand, IProgress<EngineProgress>, IEngineRun> startEngine, Func<string, DateTime?>? lastWritten = null)
    {
        _startEngine = startEngine;
        _lastWritten = lastWritten ?? LastWrittenOnDisk;
    }

    public VerifyStage Stage
    {
        get => _stage;
        private set
        {
            if (Set(ref _stage, value))
            {
                Announce(nameof(IsRunning));
                Announce(nameof(ProblemsHeading));
                Announce(nameof(CompleteHeading));
            }
        }
    }

    public bool IsRunning => Stage is VerifyStage.Verifying or VerifyStage.Repairing;

    /// <summary>The title over the problem cards: after a repair, they are the files it fixed.</summary>
    public string ProblemsHeading => Stage == VerifyStage.Repaired ? "Reparados" : "Com problema";

    /// <summary>The title over the intact files: after a repair, the files it fixed are not among them.</summary>
    public string CompleteHeading => Stage == VerifyStage.Repaired ? "Já estavam íntegros" : "Íntegros";

    public string Heading { get => _heading; private set => Set(ref _heading, value); }

    /// <summary>The recovery file being verified or repaired, in full.</summary>
    public string? RecoveryFile { get => _recoveryFile; private set => Set(ref _recoveryFile, value); }

    /// <summary>What the engine is doing right now, e.g. "Lendo praia 01.jpg".</summary>
    public string ActivityText { get => _activityText; private set => Set(ref _activityText, value); }

    /// <summary>How far the current activity is, from 0 to 100; null before the engine reports any.</summary>
    public double? Percent
    {
        get => _percent;
        private set
        {
            if (Set(ref _percent, value))
            {
                Announce(nameof(IsPercentUnknown));
            }
        }
    }

    /// <summary>No percentage yet: the bar moves on its own.</summary>
    public bool IsPercentUnknown => Percent is null;

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (Set(ref _isPaused, value))
            {
                Announce(nameof(PauseText));
            }
        }
    }

    public string PauseText => IsPaused ? "Continuar" : "Pausar";

    /// <summary>The result's headline; null while running and after a cancel.</summary>
    public Banner? Banner
    {
        get => _banner;
        private set
        {
            if (Set(ref _banner, value))
            {
                Announce(nameof(HasBanner));
            }
        }
    }

    public bool HasBanner => Banner is not null;

    public bool CanRepair { get => _canRepair; private set => Set(ref _canRepair, value); }

    public FileCounts? Counts { get => _counts; private set => Set(ref _counts, value); }

    /// <summary>Damaged, then missing, then misnamed files, each by name; after a repair, the files it fixed.</summary>
    public IReadOnlyList<ProblemFile> ProblemFiles { get => _problemFiles; private set => Set(ref _problemFiles, value); }

    /// <summary>The intact files, by name.</summary>
    public IReadOnlyList<CompleteFile> CompleteFiles { get => _completeFiles; private set => Set(ref _completeFiles, value); }

    /// <summary>How many intact files and how much they hold, e.g. "409 arquivos · 2,9 GB".</summary>
    public string CompleteSummary { get => _completeSummary; private set => Set(ref _completeSummary, value); }

    /// <summary>What went wrong, in technical terms, when something failed; otherwise null.</summary>
    public string? Details { get => _details; private set => Set(ref _details, value); }

    /// <summary>
    /// Verifies the recovery set of <paramref name="recoveryFile"/>. Ends when the engine ends; any failure
    /// is shown on the screen (<see cref="Banner"/>, <see cref="Details"/>), not thrown.
    /// </summary>
    public Task VerifyAsync(string recoveryFile)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("A task is already running.");
        }

        RecoveryFile = recoveryFile;
        return RunAsync(new EngineCommand.Verify(recoveryFile));
    }

    /// <summary>Repairs the set just verified. Only when <see cref="CanRepair"/>.</summary>
    public Task RepairAsync()
    {
        if (!CanRepair || RecoveryFile is null)
        {
            throw new InvalidOperationException("Nothing to repair.");
        }

        return RunAsync(new EngineCommand.Repair(RecoveryFile));
    }

    public void TogglePause()
    {
        if (_run is null)
        {
            return;
        }

        _run.SetPaused(!_run.IsPaused);

        IsPaused = _run.IsPaused;
    }

    public void Cancel() => _run?.Cancel();

    /// <summary>Ends the engine at once if it is still running, e.g. when the window closes.</summary>
    public void Dispose() => _run?.Dispose();

    private async Task RunAsync(EngineCommand command)
    {
        var isRepair = command is EngineCommand.Repair;
        var failedHeading = isRepair ? "Não foi possível reparar" : "Não foi possível verificar";
        var runNumber = ++_runNumber;
        Stage = isRepair ? VerifyStage.Repairing : VerifyStage.Verifying;
        Heading = isRepair ? "Reparando" : "Verificando";
        ActivityText = isRepair ? "Conferindo os arquivos antes de reparar" : "Lendo o arquivo de recuperação";
        Percent = null;
        IsPaused = false;
        Banner = null;
        CanRepair = false;
        Details = null;
        if (!isRepair)
        {
            ShowFiles(null);
        }

        try
        {
            // An update posted just before the task ended may arrive after it: ignore it then.
            _run = _startEngine(command, new UiProgress<EngineProgress>(progress =>
            {
                if (runNumber == _runNumber && IsRunning)
                {
                    Percent = progress.Percent;
                    ActivityText = ActivityTextOf(progress.Activity);
                }
            }));
        }
        catch (Exception failure)
        {
            // Shown on screen with its details: the app has nothing better to do with it.
            Fail(failedHeading,
                new Banner(BannerKind.Error, isRepair ? "Não foi possível iniciar o reparo." : "Não foi possível iniciar a verificação.", "O ArkPar pode estar instalado de forma incompleta."),
                failure.ToString());
            return;
        }

        try
        {
            var result = await _run.Completion.ConfigureAwait(true);
            EndRun();
            await ShowAsync(result, isRepair, failedHeading).ConfigureAwait(true);
        }
        catch (Exception failure)
        {
            // Never leave the screen stuck on "Verificando": whatever failed is shown, with its details.
            EndRun();
            Fail(failedHeading, new Banner(BannerKind.Error, "O motor terminou, mas o resultado não pôde ser lido.", Detail: null), failure.ToString());
        }
    }

    private void EndRun()
    {
        _run?.Dispose();
        _run = null;
        IsPaused = false;
    }

    private async Task ShowAsync(EngineResult result, bool isRepair, string failedHeading)
    {
        switch (result.Outcome)
        {
            case EngineOutcome.Canceled:
                Stage = VerifyStage.Canceled;
                Heading = isRepair ? "Reparo cancelado" : "Verificação cancelada";

                // The engine may have fixed some files before it stopped, so the earlier result no longer holds.
                Banner = isRepair ? new Banner(BannerKind.Warning, "O reparo foi interrompido.", VerifyAgain) : null;
                ShowFiles(null);
                return;

            case EngineOutcome.Error:
                Fail(failedHeading,
                    new Banner(BannerKind.Error, "O arquivo de recuperação não pôde ser lido.", "Confira se ele existe e se o disco está acessível."),
                    result.Output);
                return;
        }

        // Error and Canceled are the only outcomes without a report (EngineRun).
        var report = result.Report ?? throw new InvalidOperationException($"No report for outcome {result.Outcome}.");
        var problems = report.SourceFiles.Count - report.CompleteFiles.Count;

        if (isRepair && result.Outcome != EngineOutcome.Completed)
        {
            // The report is the state before the repair, and the engine may have fixed some files before it
            // failed: showing that list would mislead, so the screen asks for a new verification instead.
            Stage = VerifyStage.Failed;
            Heading = "O reparo falhou";
            Banner = new Banner(BannerKind.Error, "Não foi possível consertar todos os arquivos.", BlocksLacking(report.RecoveryBlocks) is { } lacking ? lacking + " " + VerifyAgain : VerifyAgain);
            Details = result.Output;
            ShowFiles(null);
            return;
        }

        var allFixed = isRepair && problems > 0;
        var lastWritten = _lastWritten;
        var files = await Task.Run(() => FilesOf(report, lastWritten, allFixed)).ConfigureAwait(true);

        switch (result.Outcome)
        {
            case EngineOutcome.Completed when allFixed:
                Stage = VerifyStage.Repaired;
                Heading = "Reparo concluído";
                Banner = new Banner(BannerKind.Success, PtBrazil.Count(problems, "arquivo consertado.", "arquivos consertados."), Recycled(report.DamagedFiles.Count + report.AppendedFiles.Count));
                break;

            case EngineOutcome.Completed:
                Stage = isRepair ? VerifyStage.Repaired : VerifyStage.Verified;
                Heading = isRepair ? "Reparo concluído" : "Verificação concluída";
                Banner = new Banner(BannerKind.Success, AllComplete(report.SourceFiles.Count), result.RecoveryFilesIncomplete ? "Um dos arquivos de recuperação está danificado ou faltando." : null);
                break;

            case EngineOutcome.RepairPossible:
                Stage = VerifyStage.Verified;
                Heading = "Verificação concluída";
                Banner = new Banner(BannerKind.Warning, WithProblem(problems) + " O reparo é possível.", BlocksNeeded(report));
                CanRepair = true;
                break;

            default:
                Stage = VerifyStage.Verified;
                Heading = "Verificação concluída";
                Banner = new Banner(BannerKind.Error, WithProblem(problems) + " O reparo não é possível.", BlocksLacking(report.RecoveryBlocks));
                break;
        }

        ShowFiles(files);
    }

    private void Fail(string heading, Banner banner, string details)
    {
        Stage = VerifyStage.Failed;
        Heading = heading;
        Banner = banner;
        Details = details;
        ShowFiles(null);
    }

    private void ShowFiles(Files? files)
    {
        Counts = files?.Counts;
        ProblemFiles = files?.Problems ?? [];
        CompleteFiles = files?.Complete ?? [];
        CompleteSummary = files?.CompleteSummary ?? "";
    }

    private sealed record Files(FileCounts Counts, IReadOnlyList<ProblemFile> Problems, IReadOnlyList<CompleteFile> Complete, string CompleteSummary);

    /// <summary>Reads the dates from disk, so run it off the screen's thread.</summary>
    private static Files FilesOf(VerificationReport report, Func<string, DateTime?> lastWritten, bool allFixed)
    {
        string NameOf(string file) => Path.GetRelativePath(report.BaseDirectory, file);
        long? SizeOf(string file) => report.FileSizes.TryGetValue(file, out var size) ? size : null;

        string Meta(string sizeOf, string? onDisk)
        {
            var parts = new List<string>(2);
            if (SizeOf(sizeOf) is { } size)
            {
                parts.Add(SizeText.Of(size));
            }

            if (onDisk is not null && lastWritten(onDisk) is { } date)
            {
                parts.Add(DateText(date));
            }

            return string.Join(" · ", parts);
        }

        IEnumerable<string> ByName(IEnumerable<string> files) => files.Order(StringComparer.CurrentCultureIgnoreCase);

        ProblemFile Problem(string file, ProblemKind kind, string tag, string? image) =>
            allFixed
                ? new ProblemFile(NameOf(file), ProblemKind.Repaired, "Reparado", file, Meta(file, file))
                : new ProblemFile(NameOf(file), kind, tag, image, Meta(file, image));

        IReadOnlyList<ProblemFile> problems =
        [
            .. ByName(report.DamagedFiles.Concat(report.AppendedFiles)).Select(file => Problem(file, ProblemKind.Damaged, "Danificado", file)),
            .. ByName(report.MissingFiles).Select(file => Problem(file, ProblemKind.Missing, "Faltando", null)),
            .. ByName(report.MisnamedFiles.Keys).Select(file => Problem(file, ProblemKind.Misnamed, "Nome trocado", report.MisnamedFiles[file])),
        ];

        IReadOnlyList<CompleteFile> complete =
        [
            .. report.CompleteFiles
                .OrderBy(NameOf, StringComparer.CurrentCultureIgnoreCase)
                .Select(file => new CompleteFile(
                    NameOf(file),
                    SizeOf(file) is { } size ? SizeText.Of(size) : "",
                    lastWritten(file) is { } date ? DateText(date) : "")),
        ];

        var counts = allFixed
            ? new FileCounts(report.SourceFiles.Count, Damaged: 0, Missing: 0, Misnamed: 0)
            : new FileCounts(report.CompleteFiles.Count, report.DamagedFiles.Count + report.AppendedFiles.Count, report.MissingFiles.Count, report.MisnamedFiles.Count);

        var completeSize = report.CompleteFiles.Sum(file => SizeOf(file) ?? 0);
        var summary = PtBrazil.Count(complete.Count, "arquivo", "arquivos") + " · " + SizeText.Of(completeSize);
        return new Files(counts, problems, complete, summary);
    }

    /// <summary>The engine's activity, in the user's words: its English phrases, or the file it is reading.</summary>
    private static string ActivityTextOf(string activity) => activity switch
    {
        EngineProgress.CheckingFiles => "Conferindo os arquivos",

        // print_progress_text in source/par2j (reedsolomon.c, rs_decode.c, repair.c).
        "Computing matrix" => "Preparando o reparo",
        "Recovering slice" or "Restoring slice" => "Recriando as partes perdidas",
        _ => "Lendo " + activity,
    };

    private static string AllComplete(int files) => files == 1 ? "O arquivo está íntegro." : $"Os {files} arquivos estão íntegros.";

    private static string WithProblem(int files) => PtBrazil.Count(files, "arquivo com problema.", "arquivos com problema.");

    private static string? BlocksNeeded(VerificationReport report) => report.RecoveryBlocks switch
    {
        null => null,

        // Nothing lost: the files are only misnamed, or only need trimming, or their parts were found elsewhere.
        { Needed: 0 } when report.MisnamedFiles.Count == report.SourceFiles.Count - report.CompleteFiles.Count =>
            "Basta devolver os nomes originais; nenhum bloco de recuperação é usado.",
        { Needed: 0 } => "Nenhum bloco de recuperação é usado.",
        var blocks => $"Precisa de {blocks.Needed} dos {blocks.Available} blocos de recuperação disponíveis.",
    };

    private static string? BlocksLacking(RecoveryBlocks? blocks) => blocks is { Lacking: > 0 }
        ? (blocks.Lacking == 1 ? "Falta 1 bloco de recuperação" : $"Faltam {blocks.Lacking} blocos de recuperação")
            + $": o reparo precisa de {blocks.Needed}, e há {blocks.Available}."
        : null;

    private static string? Recycled(int damaged) => damaged switch
    {
        0 => null,
        1 => "A versão danificada foi para a Lixeira.",
        _ => "As versões danificadas foram para a Lixeira.",
    };

    private const string VerifyAgain = "Verifique de novo para ver como os arquivos ficaram.";

    private static string DateText(DateTime date) => date.ToString("d", PtBrazil.Culture);

    private static DateTime? LastWrittenOnDisk(string file)
    {
        var info = new FileInfo(file);
        return info.Exists ? info.LastWriteTime : null;
    }
}
