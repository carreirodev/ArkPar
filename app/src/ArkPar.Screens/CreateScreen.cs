// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;

namespace ArkPar.Screens;

/// <summary>Where the Create screen is.</summary>
public enum CreateStage
{
    /// <summary>Nothing chosen yet.</summary>
    Idle,

    /// <summary>Reading what was chosen from disk.</summary>
    Reading,

    /// <summary>Files chosen: the user sets the redundancy, then creates.</summary>
    Ready,
    Creating,
    Created,
    Canceled,

    /// <summary>The engine could not create the set, or the old set could not be replaced.</summary>
    Failed,
}

/// <summary>One option of a list on the screen, e.g. a block size; a null value leaves it to the engine.</summary>
public sealed record Choice(string Text, int? Value)
{
    public override string ToString() => Text;
}

/// <summary>
/// The Create screen, in single set mode: one recovery set covering everything chosen, subfolders included,
/// saved in the folder chosen. Use it from the screen's thread; the engine's progress is brought back to it.
/// </summary>
public sealed class CreateScreen : Observable, IDisposable
{
    /// <summary>Where the redundancy starts. The engine has no default: without one it makes no recovery blocks.</summary>
    public const int DefaultRedundancy = 10;

    public const int MinRedundancy = 1;
    public const int MaxRedundancy = 100;

    private const string Automatic = "Automático";

    /// <summary>How long a killed engine gets to let go of its files before the partial ones are removed.</summary>
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    private readonly Func<EngineCommand, IProgress<EngineProgress>, IEngineRun> _startEngine;
    private readonly Func<EngineCommand.Create, CancellationToken, Task<RecoveryEstimate>> _estimate;
    private readonly Action<IReadOnlyList<string>> _recycle;
    private IEngineRun? _run;
    private EngineCommand.Create? _creating;
    private int _runNumber;
    private int _readNumber;

    // The estimate: one trial at a time; a change while one runs asks for one more, with the latest settings.
    private Task? _estimating;
    private bool _estimateAgain;
    private int _estimateNumber;
    private CancellationTokenSource? _estimateStop;

    private CreateStage _stage;
    private Selection _selection = Selection.Empty;
    private int _redundancy = DefaultRedundancy;
    private Choice _blockSize;
    private int? _recoveryFileCount;
    private Choice _memory;
    private RecoveryEstimate? _estimateResult;
    private bool _isEstimating;
    private string? _estimateProblem;
    private bool _replacesExisting;
    private string _heading = "Criar";
    private string _activityText = "";
    private double? _percent;
    private bool _isPaused;
    private Banner? _banner;
    private string? _details;

    /// <param name="startEngine">Starts an engine task (<see cref="EngineRun.Start"/> in the app).</param>
    /// <param name="estimate">Runs the engine's trial of a create (<see cref="EngineTrial.RunAsync"/> in the app).</param>
    /// <param name="recycle">Sends files to the Recycle Bin (<see cref="RecycleBin.Send"/> in the app).</param>
    public CreateScreen(
        Func<EngineCommand, IProgress<EngineProgress>, IEngineRun> startEngine,
        Func<EngineCommand.Create, CancellationToken, Task<RecoveryEstimate>> estimate,
        Action<IReadOnlyList<string>> recycle)
    {
        _startEngine = startEngine;
        _estimate = estimate;
        _recycle = recycle;
        _blockSize = BlockSizes[0];
        _memory = MemoryLimits[0];
    }

    /// <summary>The block sizes offered under Avançado. The engine raises one too small for the files.</summary>
    public static IReadOnlyList<Choice> BlockSizes { get; } =
    [
        new(Automatic, null),
        .. new[] { 64, 256, 1024, 4096, 16384 }.Select(kb => new Choice(Screens.SizeText.Of(kb * 1024L), kb * 1024)),
    ];

    /// <summary>The memory limits offered under Avançado, in eighths of the free memory (the engine's <c>/m</c>).</summary>
    public static IReadOnlyList<Choice> MemoryLimits { get; } =
    [
        new(Automatic, null),
        .. Enumerable.Range(1, 7).Select(eighths => new Choice($"Até {(eighths * 12.5).ToString("0.#", PtBrazil.Culture)}% da memória livre", eighths)),
    ];

    public CreateStage Stage
    {
        get => _stage;
        private set
        {
            if (Set(ref _stage, value))
            {
                Announce(nameof(IsRunning));
                Announce(nameof(IsReading));
                Announce(nameof(CanChoose));
                Announce(nameof(CanCreate));
            }
        }
    }

    public bool IsRunning => Stage == CreateStage.Creating;

    public bool IsReading => Stage == CreateStage.Reading;

    /// <summary>Files can be added or the choice cleared: not while reading or creating.</summary>
    public bool CanChoose => Stage is not (CreateStage.Reading or CreateStage.Creating);

    public string Heading { get => _heading; private set => Set(ref _heading, value); }

    public Selection Selection
    {
        get => _selection;
        private set
        {
            if (Set(ref _selection, value))
            {
                Announce(nameof(Tree));
                Announce(nameof(Summary));
                Announce(nameof(HasSelection));
                Announce(nameof(SelectionProblem));
                Announce(nameof(SelectionWarning));
                Announce(nameof(RecoveryFile));
                Announce(nameof(SaveIn));
                Announce(nameof(CanCreate));
            }
        }
    }

    public bool HasSelection => Selection.Roots.Count > 0;

    /// <summary>What the set covers, as a tree of folders and files.</summary>
    public IReadOnlyList<SelectionNode> Tree => Selection.Tree;

    /// <summary>How many files and how much they hold, e.g. "412 arquivos · 3,1 GB".</summary>
    public string Summary => HasSelection ? Selection.Summary : "";

    /// <summary>Why no set can be made of what was chosen; null when one can.</summary>
    public string? SelectionProblem => Selection.Problem;

    /// <summary>Folders left out because they could not be read; null when there are none.</summary>
    public string? SelectionWarning => Selection.Unreadable switch
    {
        [] => null,
        [var folder] => $"A pasta \"{Path.GetFileName(folder)}\" não pôde ser lida e ficou de fora.",
        var folders => $"{folders.Count} pastas não puderam ser lidas e ficaram de fora.",
    };

    /// <summary>The set's index file, in full: where the recovery files will be saved.</summary>
    public string? RecoveryFile => Selection.RecoveryFile;

    /// <summary>The folder the recovery files go in, by name.</summary>
    public string SaveIn => Selection.BaseDirectory is { } folder ? Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name : folder : "";

    /// <summary>The redundancy, in whole percent, from <see cref="MinRedundancy"/> to <see cref="MaxRedundancy"/>.</summary>
    public int Redundancy
    {
        get => _redundancy;
        set
        {
            if (Set(ref _redundancy, Math.Clamp(value, MinRedundancy, MaxRedundancy)))
            {
                Announce(nameof(RedundancyText));
                RequestEstimate();
            }
        }
    }

    public string RedundancyText => Redundancy.ToString(PtBrazil.Culture) + "%";

    public Choice BlockSize
    {
        get => _blockSize;
        set
        {
            if (Set(ref _blockSize, value))
            {
                RequestEstimate();
            }
        }
    }

    /// <summary>How many recovery files to make, from 1 to 999; null leaves it to the engine.</summary>
    public int? RecoveryFileCount
    {
        get => _recoveryFileCount;
        set
        {
            if (Set(ref _recoveryFileCount, value is { } count ? Math.Clamp(count, 1, 999) : null))
            {
                RequestEstimate();
            }
        }
    }

    public Choice Memory
    {
        get => _memory;
        set
        {
            if (Set(ref _memory, value))
            {
                RequestEstimate();
            }
        }
    }

    /// <summary>What the engine says the create will make; null until it has said, or when it could not.</summary>
    public RecoveryEstimate? Estimate
    {
        get => _estimateResult;
        private set
        {
            if (Set(ref _estimateResult, value))
            {
                Announce(nameof(SizeText));
                Announce(nameof(RepairableText));
                Announce(nameof(BlocksText));
                Announce(nameof(RecoveryFilesText));
                Announce(nameof(CanCreate));
            }
        }
    }

    /// <summary>The engine is working out the sizes for the latest settings.</summary>
    public bool IsEstimating
    {
        get => _isEstimating;
        private set
        {
            if (Set(ref _isEstimating, value))
            {
                Announce(nameof(CanCreate));
            }
        }
    }

    /// <summary>Why the sizes could not be worked out; null when they could.</summary>
    public string? EstimateProblem { get => _estimateProblem; private set => Set(ref _estimateProblem, value); }

    /// <summary>How much space the recovery files will take, e.g. "≈ 310 MB".</summary>
    public string SizeText => Estimate is { } estimate ? "≈ " + Screens.SizeText.Of(estimate.TotalSize) : "—";

    /// <summary>The most lost data they can rebuild, e.g. "≈ 310 MB".</summary>
    public string RepairableText => Estimate is { } estimate ? "≈ " + Screens.SizeText.Of(estimate.RepairableSize) : "—";

    /// <summary>The blocks the engine will use, e.g. "240 blocos de 24 KB, 24 para recuperação".</summary>
    public string BlocksText => Estimate is { } estimate
        ? $"{estimate.SourceBlocks.ToString("N0", PtBrazil.Culture)} blocos de {Screens.SizeText.Of(estimate.BlockSize)}, {estimate.RecoveryBlocks.ToString("N0", PtBrazil.Culture)} para recuperação"
        : "";

    /// <summary>How many recovery files the engine will make, the index aside, e.g. "1 arquivo de recuperação, além do índice".</summary>
    public string RecoveryFilesText => Estimate is { } estimate
        ? (estimate.RecoveryFiles == 1 ? "1 arquivo de recuperação" : $"{estimate.RecoveryFiles} arquivos de recuperação") + ", além do índice"
        : "";

    /// <summary>A set of the same name is already in the folder: creating replaces it, the old files going to the Recycle Bin.</summary>
    public bool ReplacesExisting
    {
        get => _replacesExisting;
        private set
        {
            if (Set(ref _replacesExisting, value))
            {
                Announce(nameof(CreateText));
            }
        }
    }

    public string CreateText => ReplacesExisting ? "Substituir arquivos de recuperação" : "Criar arquivos de recuperação";

    /// <summary>Only once the engine has worked out the sizes for the current settings, so what is shown is what is made.</summary>
    public bool CanCreate =>
        CanChoose && Selection.Problem is null && HasSelection && Estimate is not null && !IsEstimating;

    /// <summary>What the engine is doing right now, e.g. "Criando os blocos de recuperação".</summary>
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

    /// <summary>The result's headline; null until a create ends.</summary>
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

    /// <summary>What went wrong, in technical terms, when something failed; otherwise null.</summary>
    public string? Details { get => _details; private set => Set(ref _details, value); }

    /// <summary>
    /// Adds <paramref name="paths"/> (files or folders) to what the set covers, reading them from disk off the
    /// screen's thread, then asks the engine how big the set will be.
    /// </summary>
    public async Task AddAsync(IEnumerable<string> paths)
    {
        if (!CanChoose)
        {
            throw new InvalidOperationException("Files cannot be added while reading or creating.");
        }

        var readNumber = ++_readNumber;
        string[] all = [.. Selection.Roots, .. paths];
        StopEstimate();
        Estimate = null;
        EstimateProblem = null;
        Banner = null;
        Details = null;
        Stage = CreateStage.Reading;
        Heading = "Criar";

        Selection selection;
        try
        {
            selection = await Task.Run(() => Selection.Read(all)).ConfigureAwait(true);
        }
        catch (Exception failure)
        {
            // Shown on screen with its details: a folder vanished mid-read, a disk failed...
            if (readNumber == _readNumber)
            {
                Stage = CreateStage.Failed;
                Banner = new Banner(BannerKind.Error, "Não foi possível ler os arquivos escolhidos.", "Confira se o disco está acessível e tente de novo.");
                Details = failure.ToString();
            }

            return;
        }

        if (readNumber != _readNumber)
        {
            return;
        }

        Selection = selection;
        Stage = HasSelection ? CreateStage.Ready : CreateStage.Idle;
        ReplacesExisting = Selection.ExistingRecoveryFiles().Count > 0;
        RequestEstimate();
    }

    /// <summary>Forgets what was chosen.</summary>
    public void Clear()
    {
        if (!CanChoose)
        {
            throw new InvalidOperationException("The choice cannot be cleared while reading or creating.");
        }

        StopEstimate();
        Selection = Selection.Empty;
        Estimate = null;
        EstimateProblem = null;
        ReplacesExisting = false;
        Banner = null;
        Details = null;
        Heading = "Criar";
        Stage = CreateStage.Idle;
    }

    /// <summary>
    /// Creates the set. Ends when the engine ends; any failure is shown on the screen (<see cref="Banner"/>,
    /// <see cref="Details"/>), not thrown. Only when <see cref="CanCreate"/>.
    /// </summary>
    public async Task CreateAsync()
    {
        if (!CanCreate)
        {
            throw new InvalidOperationException("Nothing to create.");
        }

        var create = CommandFor(Selection);
        var existing = Selection.ExistingRecoveryFiles();
        var runNumber = ++_runNumber;
        StopEstimate();
        Stage = CreateStage.Creating;
        Heading = "Criando";
        ActivityText = existing.Count > 0 ? "Levando o conjunto antigo para a Lixeira" : "Preparando";
        Percent = null;
        IsPaused = false;
        Banner = null;
        Details = null;

        try
        {
            if (existing.Count > 0)
            {
                // The engine would write over the old index but leave the old recovery files beside the new ones.
                await Task.Run(() => _recycle(existing)).ConfigureAwait(true);
                ActivityText = "Preparando";
            }

            _creating = create;
            _run = _startEngine(create, new UiProgress<EngineProgress>(progress =>
            {
                // An update posted just before the task ended may arrive after it: ignore it then.
                if (runNumber == _runNumber && IsRunning)
                {
                    Percent = progress.Percent;
                    ActivityText = ActivityTextOf(progress.Activity);
                }
            }));
        }
        catch (Exception failure)
        {
            Fail(existing.Count > 0 && _run is null
                    ? new Banner(BannerKind.Error, "Não foi possível levar o conjunto antigo para a Lixeira.", "Confira se algum dos arquivos está aberto em outro programa.")
                    : new Banner(BannerKind.Error, "Não foi possível iniciar a criação.", "O ArkPar pode estar instalado de forma incompleta."),
                failure.ToString());
            return;
        }

        try
        {
            var result = await _run.Completion.ConfigureAwait(true);
            EndRun();
            await ShowAsync(result, create, replaced: existing.Count > 0).ConfigureAwait(true);
        }
        catch (Exception failure)
        {
            // Never leave the screen stuck on "Criando": whatever failed is shown, with its details.
            EndRun();
            await RemovePartialFilesAsync(create).ConfigureAwait(true);
            Fail(new Banner(BannerKind.Error, "A criação terminou de forma inesperada.", Detail: null), failure.ToString());
        }
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

    /// <summary>
    /// Ends the engine at once if it is still running, e.g. when the window closes, and removes the recovery
    /// files it left half written: ended this way, the engine does not remove them itself.
    /// </summary>
    public void Dispose()
    {
        StopEstimate();
        if (_run is not { } run || _creating is not { } create)
        {
            return;
        }

        run.Dispose();

        // The run may end with an error of its own; the partial files go either way, so only its end matters.
        if (run.Completion.ContinueWith(_ => { }, TaskScheduler.Default).Wait(KillWait))
        {
            DeletePartialFiles(create);
        }
    }

    private void EndRun()
    {
        _run?.Dispose();
        _run = null;
        _creating = null;
        IsPaused = false;
    }

    private async Task ShowAsync(EngineResult result, EngineCommand.Create create, bool replaced)
    {
        const string OldSetInRecycleBin = "O conjunto antigo está na Lixeira, se quiser recuperá-lo.";
        switch (result.Outcome)
        {
            case EngineOutcome.Completed:
                var recoveryFiles = Selection.ExistingRecoveryFiles();
                var size = await Task.Run(() => recoveryFiles.Sum(file => new FileInfo(file).Length)).ConfigureAwait(true);
                Stage = CreateStage.Created;
                Heading = "Conjunto criado";
                ReplacesExisting = true;
                Banner = new Banner(
                    BannerKind.Success,
                    $"Os arquivos de recuperação protegem {Selection.Summary}.",
                    $"{(recoveryFiles.Count == 1 ? "1 arquivo" : $"{recoveryFiles.Count} arquivos")} · {Screens.SizeText.Of(size)}, em {Selection.BaseDirectory}");
                return;

            case EngineOutcome.Canceled:
                await RemovePartialFilesAsync(create).ConfigureAwait(true);
                Stage = CreateStage.Canceled;
                Heading = "Criação cancelada";
                ReplacesExisting = false;
                Banner = new Banner(BannerKind.Warning, "Nenhum arquivo de recuperação foi gravado.", replaced ? OldSetInRecycleBin : null);
                RequestEstimate();
                return;

            default:
                await RemovePartialFilesAsync(create).ConfigureAwait(true);
                ReplacesExisting = false;
                Fail(
                    new Banner(BannerKind.Error, "O motor não conseguiu criar os arquivos de recuperação.",
                        "Confira se há espaço no disco e se a pasta permite gravar." + (replaced ? " " + OldSetInRecycleBin : "")),
                    result.Output);
                return;
        }
    }

    private void Fail(Banner banner, string details)
    {
        Stage = CreateStage.Failed;
        Heading = "Não foi possível criar";
        Banner = banner;
        Details = details;
        RequestEstimate();
    }

    /// <summary>
    /// Removes what a create that did not finish left behind. Every file of the set's name is from this run:
    /// an older set of that name went to the Recycle Bin before it started.
    /// </summary>
    private static Task RemovePartialFilesAsync(EngineCommand.Create create) => Task.Run(() => DeletePartialFiles(create));

    private static void DeletePartialFiles(EngineCommand.Create create)
    {
        foreach (var file in Selection.RecoverySetFiles(create.RecoveryFile))
        {
            File.Delete(file);
        }
    }

    private EngineCommand.Create CommandFor(Selection selection) =>
        new(
            selection.RecoveryFile ?? throw new InvalidOperationException("No set can be made of this selection."),
            selection.BaseDirectory ?? throw new InvalidOperationException("No set can be made of this selection."),
            selection.Files,
            Redundancy,
            new CreateOptions(BlockSize.Value, RecoveryFileCount, Memory.Value));

    private void RequestEstimate()
    {
        _estimateNumber++;
        if (Stage is not (CreateStage.Ready or CreateStage.Created or CreateStage.Canceled or CreateStage.Failed)
            || !HasSelection || Selection.Problem is not null)
        {
            return;
        }

        if (_estimating is not null)
        {
            _estimateAgain = true;
            return;
        }

        _estimating = EstimateAsync();
    }

    private async Task EstimateAsync()
    {
        IsEstimating = true;
        try
        {
            do
            {
                _estimateAgain = false;
                var number = _estimateNumber;
                using var stop = new CancellationTokenSource();
                _estimateStop = stop;
                try
                {
                    var estimate = await _estimate(CommandFor(Selection), stop.Token).ConfigureAwait(true);
                    if (number == _estimateNumber)
                    {
                        Estimate = estimate;
                        EstimateProblem = null;
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    // Stopped because what was chosen changed: its answer is not wanted, but a newer one may be.
                    continue;
                }
                catch (Exception failure)
                {
                    if (number == _estimateNumber)
                    {
                        Estimate = null;
                        EstimateProblem = "Não foi possível calcular o tamanho com estes ajustes.";
                        Details = failure is EngineTrialException trial ? trial.Output : failure.ToString();
                    }
                }
                finally
                {
                    _estimateStop = null;
                }
            }
            while (_estimateAgain);
        }
        finally
        {
            _estimating = null;
            IsEstimating = false;
        }
    }

    private void StopEstimate()
    {
        _estimateNumber++;
        _estimateAgain = false;
        _estimateStop?.Cancel();
    }

    /// <summary>The engine's activity, in the user's words (print_progress_text in source/par2j: par2.c, create.c).</summary>
    private static string ActivityTextOf(string activity) => activity switch
    {
        "Computing file hash" => "Lendo os arquivos",
        "Creating recovery slice" => "Criando os blocos de recuperação",
        "Constructing recovery file" => "Gravando os arquivos de recuperação",
        "Making index file" => "Gravando o índice",
        _ => "Lendo " + activity,
    };
}
