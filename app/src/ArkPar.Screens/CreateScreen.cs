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

    /// <summary>The engine could not create the set, or the set it replaces could not be moved away.</summary>
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

    /// <summary>The most recovery files the screen lets the user ask for.</summary>
    public const int MaxRecoveryFiles = 999;

    private const string Automatic = "Automático";

    /// <summary>How long a killed engine gets to let go of its files before the partial ones are removed.</summary>
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    private readonly Func<EngineCommand, IProgress<EngineProgress>, IEngineRun> _startEngine;
    private readonly Func<EngineCommand.Create, CancellationToken, Task<RecoveryEstimate>> _runTrial;
    private readonly Action<IReadOnlyList<string>> _recycle;
    private IEngineRun? _run;
    private EngineCommand.Create? _creating;
    private int _runNumber;
    private bool _disposed;

    // The estimate: one trial at a time; a change while one runs asks for one more, with the latest settings.
    private bool _estimateAgain;
    private int _estimateNumber;
    private CancellationTokenSource? _estimateStop;

    // Details shows why the last estimate failed: a later one that works removes it.
    private bool _detailsFromEstimate;

    private CreateStage _stage;
    private Selection _selection = Selection.Empty;
    private int _redundancy = DefaultRedundancy;
    private Choice _blockSize;
    private int? _recoveryFileCount;
    private Choice _memory;
    private RecoveryEstimate? _estimate;
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
    /// <param name="runTrial">Runs the engine's trial of a create (<see cref="EngineTrial.RunAsync"/> in the app).</param>
    /// <param name="recycle">Sends files to the Recycle Bin (or deletes them on a drive that has none).</param>
    public CreateScreen(
        Func<EngineCommand, IProgress<EngineProgress>, IEngineRun> startEngine,
        Func<EngineCommand.Create, CancellationToken, Task<RecoveryEstimate>> runTrial,
        Action<IReadOnlyList<string>> recycle)
    {
        _startEngine = startEngine;
        _runTrial = runTrial;
        _recycle = recycle;
        _blockSize = BlockSizes[0];
        _memory = MemoryLimits[0];
    }

    /// <summary>The block sizes offered under Avançado, in bytes. The engine raises one too small for the files.</summary>
    public static IReadOnlyList<Choice> BlockSizes { get; } =
    [
        new(Automatic, null),
        .. new[] { 64, 256, 1024, 4096, 16384 }.Select(kb => new Choice(SizeText.Of(kb * 1024L), kb * 1024)),
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
    public string SaveIn => Selection.BaseDirectory is { } folder ? Selection.FolderName(folder) : "";

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

    /// <summary>How many recovery files to make, from 1 to <see cref="MaxRecoveryFiles"/>; null leaves it to the engine.</summary>
    public int? RecoveryFileCount
    {
        get => _recoveryFileCount;
        set
        {
            if (Set(ref _recoveryFileCount, value is { } count ? Math.Clamp(count, 1, MaxRecoveryFiles) : null))
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
        get => _estimate;
        private set
        {
            if (Set(ref _estimate, value))
            {
                Announce(nameof(SpaceText));
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
    public string SpaceText => Estimate is { } estimate ? "≈ " + SizeText.Of(estimate.TotalSize) : "—";

    /// <summary>The most lost data they can rebuild, e.g. "≈ 310 MB".</summary>
    public string RepairableText => Estimate is { } estimate ? "≈ " + SizeText.Of(estimate.RepairableSize) : "—";

    /// <summary>The blocks the engine will use, e.g. "240 blocos de 24 KB, 24 para recuperação".</summary>
    public string BlocksText => Estimate is { } estimate
        ? $"{PtBrazil.Count(estimate.SourceBlocks, "bloco", "blocos")} de {SizeText.Of(estimate.BlockSize)}, {estimate.RecoveryBlocks.ToString("N0", PtBrazil.Culture)} para recuperação"
        : "";

    /// <summary>How many recovery files the engine will make, the index aside, e.g. "1 arquivo de recuperação, além do índice".</summary>
    public string RecoveryFilesText => Estimate is { } estimate
        ? PtBrazil.Count(estimate.RecoveryFiles, "arquivo de recuperação", "arquivos de recuperação") + ", além do índice"
        : "";

    /// <summary>A set of the same name is already in the folder: creating replaces it, its files going to the Recycle Bin.</summary>
    public bool ReplacesExisting
    {
        get => _replacesExisting;
        private set
        {
            if (Set(ref _replacesExisting, value))
            {
                Announce(nameof(CreateText));
                Announce(nameof(ReplaceWarning));
            }
        }
    }

    public string CreateText => ReplacesExisting ? "Substituir arquivos de recuperação" : "Criar arquivos de recuperação";

    /// <summary>What happens to the set being replaced; null when there is none.</summary>
    public string? ReplaceWarning => !ReplacesExisting
        ? null
        : "Já existe um conjunto com este nome nesta pasta. "
            + (HasRecycleBin ? "Os arquivos dele irão para a Lixeira." : "Os arquivos dele serão apagados, porque este disco não tem Lixeira.");

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

    /// <summary>The engine is running, so it can be paused or canceled (not while the set it replaces is moved away).</summary>
    public bool CanControl => Run is not null;

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
    public string? Details
    {
        get => _details;
        private set
        {
            Set(ref _details, value);
            _detailsFromEstimate = false;
        }
    }

    /// <summary>The engine task under way; null before it starts and once it has ended.</summary>
    private IEngineRun? Run
    {
        get => _run;
        set
        {
            if (Set(ref _run, value))
            {
                Announce(nameof(CanControl));
            }
        }
    }

    /// <summary>
    /// Whether the files of a set replaced in this folder go to the Recycle Bin: Windows keeps one only on
    /// fixed disks, and deletes at once on USB sticks, memory cards and network folders.
    /// </summary>
    private bool HasRecycleBin =>
        Selection.BaseDirectory is { } folder
        && !folder.StartsWith(@"\\", StringComparison.Ordinal)
        && new DriveInfo(folder).DriveType == DriveType.Fixed;

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

        string[] all = [.. Selection.Roots, .. paths];
        StopEstimate();
        Estimate = null;
        EstimateProblem = null;
        Banner = null;
        Details = null;
        Stage = CreateStage.Reading;
        Heading = "Criar";

        try
        {
            Selection = await Task.Run(() => Selection.Read(all)).ConfigureAwait(true);
        }
        catch (Exception failure)
        {
            // Shown on screen with its details: a folder vanished mid-read, a disk failed...
            Stage = CreateStage.Failed;
            Banner = new Banner(BannerKind.Error, "Não foi possível ler os arquivos escolhidos.", "Confira se o disco está acessível e tente de novo.");
            Details = failure.ToString();
            return;
        }

        Stage = HasSelection ? CreateStage.Ready : CreateStage.Idle;
        CheckForExistingSet();
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
        var replaced = Selection.ExistingRecoveryFiles();
        var hasRecycleBin = HasRecycleBin;
        var runNumber = ++_runNumber;
        StopEstimate();
        Stage = CreateStage.Creating;
        Heading = "Criando";
        ActivityText = replaced.Count == 0 ? "Preparando"
            : hasRecycleBin ? "Levando o conjunto anterior para a Lixeira"
            : "Apagando o conjunto anterior";
        Percent = null;
        IsPaused = false;
        Banner = null;
        Details = null;

        try
        {
            if (replaced.Count > 0)
            {
                // The engine would write over the index but leave the replaced set's recovery files beside the new ones.
                await Task.Run(() => _recycle(replaced)).ConfigureAwait(true);
                if (_disposed)
                {
                    // The window closed meanwhile: start nothing that would outlive it.
                    return;
                }

                ActivityText = "Preparando";
            }

            _creating = create;
            Run = _startEngine(create, new UiProgress<EngineProgress>(progress =>
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
            Fail(replaced.Count > 0 && Run is null
                    ? new Banner(BannerKind.Error, "Não foi possível tirar o conjunto anterior da pasta.", "Confira se algum dos arquivos dele está aberto em outro programa.")
                    : new Banner(BannerKind.Error, "Não foi possível iniciar a criação.", "O ArkPar pode estar instalado de forma incompleta."),
                failure.ToString());
            return;
        }

        var replacedNote = replaced.Count == 0 ? null
            : hasRecycleBin ? "O conjunto anterior está na Lixeira, se quiser recuperá-lo."
            : "O conjunto anterior foi apagado; crie o conjunto de novo para proteger os arquivos.";
        EngineResult result;
        try
        {
            result = await Run.Completion.ConfigureAwait(true);
        }
        catch (Exception failure)
        {
            // Never leave the screen stuck on "Criando": whatever failed is shown, with its details.
            EndRun();
            var leftover = await RemovePartialFilesAsync(create).ConfigureAwait(true);
            Fail(new Banner(BannerKind.Error, "A criação terminou de forma inesperada.", Join(replacedNote, LeftoverText(create, leftover), " ")),
                Join(failure.ToString(), leftover));
            return;
        }

        EndRun();
        await ShowAsync(result, create, replacedNote).ConfigureAwait(true);
    }

    public void TogglePause()
    {
        if (Run is null)
        {
            return;
        }

        Run.SetPaused(!Run.IsPaused);
        IsPaused = Run.IsPaused;
    }

    public void Cancel() => Run?.Cancel();

    /// <summary>
    /// Ends the engine at once if it is still running, e.g. when the window closes, and removes the recovery
    /// files it left half written: ended this way, the engine does not remove them itself.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        StopEstimate();
        if (Run is not { } run || _creating is not { } create)
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
        Run?.Dispose();
        Run = null;
        _creating = null;
        IsPaused = false;
    }

    private async Task ShowAsync(EngineResult result, EngineCommand.Create create, string? replacedNote)
    {
        if (result.Outcome == EngineOutcome.Completed)
        {
            // The set is made: nothing from here on may remove it.
            Stage = CreateStage.Created;
            Heading = "Conjunto criado";
            CheckForExistingSet();
            var recoveryFiles = Selection.RecoverySetFiles(create.RecoveryFile);
            var size = await Task.Run(() => recoveryFiles.Sum(file => new FileInfo(file).Length)).ConfigureAwait(true);
            Banner = new Banner(
                BannerKind.Success,
                $"Os arquivos de recuperação protegem {Selection.Summary}.",
                $"{PtBrazil.Count(recoveryFiles.Count, "arquivo", "arquivos")} · {SizeText.Of(size)}, em {create.BaseDirectory}");
            return;
        }

        var leftover = await RemovePartialFilesAsync(create).ConfigureAwait(true);
        if (result.Outcome == EngineOutcome.Canceled)
        {
            Stage = CreateStage.Canceled;
            Heading = "Criação cancelada";
            CheckForExistingSet();
            Banner = new Banner(BannerKind.Warning, "Nenhum arquivo de recuperação foi gravado.", Join(replacedNote, LeftoverText(create, leftover), " "));
            Details = leftover;
            RequestEstimate();
            return;
        }

        Fail(
            new Banner(BannerKind.Error, "O motor não conseguiu criar os arquivos de recuperação.",
                Join(Join("Confira se há espaço no disco e se a pasta permite gravar.", replacedNote, " "), LeftoverText(create, leftover), " ")),
            Join(result.Output, leftover));
    }

    private void Fail(Banner banner, string? details)
    {
        Stage = CreateStage.Failed;
        Heading = "Não foi possível criar";
        CheckForExistingSet();
        Banner = banner;
        Details = details;
        RequestEstimate();
    }

    /// <summary>Whether a set of this name is in the folder now, so Criar would replace it.</summary>
    private void CheckForExistingSet() => ReplacesExisting = Selection.ExistingRecoveryFiles().Count > 0;

    /// <summary>
    /// Removes what a create that did not finish left behind. Every file of the set's name is from this run:
    /// a set of that name it replaced was moved away before it started.
    /// </summary>
    /// <returns>Null when all went; otherwise why some could not, which the screen then shows.</returns>
    private static async Task<string?> RemovePartialFilesAsync(EngineCommand.Create create)
    {
        try
        {
            await Task.Run(() => DeletePartialFiles(create)).ConfigureAwait(true);
            return null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // A file held by another program (an antivirus scanning it...): the screen says which to delete.
            return failure.ToString();
        }
    }

    private static void DeletePartialFiles(EngineCommand.Create create)
    {
        foreach (var file in Selection.RecoverySetFiles(create.RecoveryFile))
        {
            File.Delete(file);
        }
    }

    private static string? LeftoverText(EngineCommand.Create create, string? leftover) => leftover is null
        ? null
        : $"Arquivos incompletos ficaram na pasta; apague os que começam com \"{Path.GetFileNameWithoutExtension(create.RecoveryFile)}\" e terminam em .par2.";

    private static string? Join(string? first, string? second, string separator = "\n\n") =>
        first is null ? second : second is null ? first : first + separator + second;

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
        if (_disposed
            || Stage is not (CreateStage.Ready or CreateStage.Created or CreateStage.Canceled or CreateStage.Failed)
            || !HasSelection || Selection.Problem is not null)
        {
            return;
        }

        // IsEstimating is set before the loop's first wait, so a trial that ends at once is never mistaken for one under way.
        if (IsEstimating)
        {
            _estimateAgain = true;
            return;
        }

        // The loop shows every failure on the screen itself.
        _ = EstimateAsync();
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
                    var estimate = await _runTrial(CommandFor(Selection), stop.Token).ConfigureAwait(true);
                    if (number == _estimateNumber)
                    {
                        var clearDetails = _detailsFromEstimate;
                        Estimate = estimate;
                        EstimateProblem = null;
                        if (clearDetails)
                        {
                            // Only an earlier failed estimate's details go: a failed create's stay.
                            Details = null;
                        }
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
                        _detailsFromEstimate = true;
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
