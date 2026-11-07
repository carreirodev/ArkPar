// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.ExceptionServices;
using ArkPar.Engine;

namespace ArkPar.Screens;

/// <summary>Where the Create screen is.</summary>
public enum CreateStage
{
    /// <summary>Nothing chosen yet.</summary>
    Idle,

    /// <summary>Reading what was chosen from disk.</summary>
    Reading,

    /// <summary>Files chosen: the user picks the mode and the redundancy, then creates.</summary>
    Ready,
    Creating,
    Created,
    Canceled,

    /// <summary>The engine could not create a set, or the set it replaces could not be moved away.</summary>
    Failed,
}

/// <summary>One option of a list on the screen, e.g. a block size; a null value leaves it to the engine.</summary>
public sealed record Choice(string Text, int? Value)
{
    public override string ToString() => Text;
}

/// <summary>A creation mode as the screen offers it, with what it does on the example every mode uses.</summary>
public sealed record ModeChoice(CreationMode Mode, string Title, string Explanation)
{
    /// <summary>The title: what a screen reader says for the option.</summary>
    public override string ToString() => Title;
}

/// <summary>
/// The Create screen: the files chosen, split into recovery sets by the mode chosen (<see cref="CreationPlan"/>),
/// all made with the same redundancy and Avançado settings, one after another. Use it from the screen's thread;
/// the engine's progress is brought back to it.
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

    private readonly Func<EngineCommand, IProgress<EngineProgress>, IEngineRun> _startEngine;
    private readonly Func<EngineCommand.Create, CancellationToken, Task<RecoveryEstimate>> _runTrial;
    private readonly Action<IReadOnlyList<string>> _recycle;
    private readonly Func<string, bool> _hasRecycleBin;
    private readonly SetReview _review;
    private IEngineRun? _run;
    private SetCreation? _creating;
    private int _runNumber;
    private bool _cancelRequested;
    private bool _disposed;

    // The estimate: one round at a time, a trial for each set; a change while one runs stops it and asks for one more.
    private bool _estimateAgain;
    private int _estimateNumber;
    private CancellationTokenSource? _estimateStop;

    // Details shows why the last estimate failed: a later one that works removes it.
    private bool _detailsFromEstimate;

    private CreateStage _stage;
    private Selection _selection = Selection.Empty;
    private CreationMode _mode = CreationMode.SingleSet;
    private CreationPlan _plan = CreationPlan.Empty;
    private int _redundancy = DefaultRedundancy;
    private Choice _blockSize;
    private int? _recoveryFileCount;
    private Choice _memory;
    private CreationEstimate? _estimate;

    // The engine's estimate of each planned set, in the plan's order; Estimate covers the ones included.
    private RecoveryEstimate[]? _setEstimates;
    private bool _isEstimating;
    private int _estimatedSets;
    private string? _estimateProblem;
    private string _heading = "Criar";
    private string? _setProgressText;
    private string _activityText = "";
    private double? _percent;
    private bool _isPaused;
    private Banner? _banner;
    private string? _details;

    /// <param name="startEngine">Starts an engine task (<see cref="EngineRun.Start"/> in the app).</param>
    /// <param name="runTrial">Runs the engine's trial of a create (<see cref="EngineTrial.RunAsync"/> in the app).</param>
    /// <param name="listSet">Lists the files a set on disk protects (<see cref="EngineListing.RunAsync"/> in the app).</param>
    /// <param name="recycle">Sends files to the Recycle Bin.</param>
    /// <param name="hasRecycleBin">Whether a folder's drive has a Recycle Bin; by default, only fixed disks do.</param>
    public CreateScreen(
        Func<EngineCommand, IProgress<EngineProgress>, IEngineRun> startEngine,
        Func<EngineCommand.Create, CancellationToken, Task<RecoveryEstimate>> runTrial,
        Func<string, CancellationToken, Task<SetListing>> listSet,
        Action<IReadOnlyList<string>> recycle,
        Func<string, bool>? hasRecycleBin = null)
    {
        _startEngine = startEngine;
        _runTrial = runTrial;
        _recycle = recycle;
        _hasRecycleBin = hasRecycleBin ?? DriveHasRecycleBin;
        _blockSize = BlockSizes[0];
        _memory = MemoryLimits[0];
        _review = new SetReview(listSet);
        _review.PropertyChanged += OnReviewChanged;
    }

    /// <summary>
    /// The creation modes, each explained on the same example: <c>D:\Fotos\2019</c> chosen, holding
    /// "01 - Réveillon" and "07 - Viagem Bahia", with "Praia do Forte" inside the latter.
    /// </summary>
    public static IReadOnlyList<ModeChoice> Modes { get; } =
    [
        new(CreationMode.SingleSet, "Um conjunto para tudo",
            "Um só conjunto protege tudo o que foi escolhido, com as subpastas. Ex.: escolhendo D:\\Fotos\\2019, sai 1 conjunto em 2019."),
        new(CreationMode.PerSubfolder, "Um por subpasta",
            "Cada subpasta ganha o seu conjunto, cobrindo tudo dentro dela. Ex.: 1 conjunto em 01 - Réveillon e 1 em 07 - Viagem Bahia, este cobrindo também Praia do Forte. Arquivos soltos em 2019 ganham um conjunto só deles."),
        new(CreationMode.PerFolder, "Um por pasta",
            "Cada pasta que tem arquivos ganha o seu conjunto, cobrindo só os arquivos dela. Ex.: 1 conjunto em 01 - Réveillon, 1 em 07 - Viagem Bahia e 1 em Praia do Forte."),
        new(CreationMode.PerFile, "Um por arquivo",
            "Cada arquivo ganha o seu conjunto, ao lado dele. Ex.: cada foto de 01 - Réveillon, 07 - Viagem Bahia e Praia do Forte ganha o seu, como IMG_4469.JPG.par2 ao lado de IMG_4469.JPG."),
    ];

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
                Announce(nameof(CanCancel));
            }
        }
    }

    public bool IsRunning => Stage == CreateStage.Creating;

    public bool IsReading => Stage == CreateStage.Reading;

    /// <summary>Files can be added, the mode changed or the choice cleared: not while reading or creating.</summary>
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
                Announce(nameof(SelectionWarning));
                Announce(nameof(SelectionProblem));
                Announce(nameof(ReplaceWarning));
                Announce(nameof(CanCreate));
                Plan = CreationPlan.For(value, Mode);
            }
        }
    }

    public bool HasSelection => Selection.Roots.Count > 0;

    /// <summary>What was chosen, as a tree of folders and files.</summary>
    public IReadOnlyList<SelectionNode> Tree => Selection.Tree;

    /// <summary>How many files and how much they hold, e.g. "412 arquivos · 3,1 GB".</summary>
    public string Summary => HasSelection ? Selection.Summary : "";

    /// <summary>Why no set can be made of what was chosen, in this mode; null when one can.</summary>
    public string? SelectionProblem => Selection.Problem ?? Plan.Problem;

    /// <summary>Folders left out because they could not be read; null when there are none.</summary>
    public string? SelectionWarning => Selection.Unreadable switch
    {
        [] => null,
        [var folder] => $"A pasta \"{Path.GetFileName(folder)}\" não pôde ser lida e ficou de fora.",
        var folders => $"{folders.Count} pastas não puderam ser lidas e ficaram de fora.",
    };

    /// <summary>How the files chosen are split into sets. Not while reading or creating.</summary>
    public CreationMode Mode
    {
        get => _mode;
        set
        {
            if (!CanChoose)
            {
                throw new InvalidOperationException("The mode cannot change while reading or creating.");
            }

            if (Set(ref _mode, value))
            {
                Plan = CreationPlan.For(Selection, value);
            }
        }
    }

    /// <summary>The sets the create will make, from what was chosen and the mode.</summary>
    public CreationPlan Plan
    {
        get => _plan;
        private set
        {
            if (!Set(ref _plan, value))
            {
                return;
            }

            Announce(nameof(Sets));
            Announce(nameof(HasSets));
            Announce(nameof(SelectionProblem));
            Announce(nameof(SaveIn));
            Announce(nameof(Destination));
            Announce(nameof(EstimatingText));
            Announce(nameof(ParFilesWarning));
            _setEstimates = null;
            Estimate = null;
            EstimateProblem = null;
            _review.Start(value.Sets);
            RequestEstimate();
        }
    }

    /// <summary>The sets the mode splits what was chosen into, each with where it goes.</summary>
    public IReadOnlyList<PlannedSet> Sets => Plan.Sets;

    public bool HasSets => Sets.Count > 0;

    /// <summary>
    /// One line for each of <see cref="Sets"/>: what is in its folder now, and whether the create makes it. A set
    /// up to date starts left out.
    /// </summary>
    public IReadOnlyList<ReviewedSet> Items => _review.Items;

    /// <summary>The sets the create will make: the ones included.</summary>
    public IReadOnlyList<PlannedSet> Included => _review.Included;

    /// <summary>The engine is listing the sets already in the folders, to compare them.</summary>
    public bool IsReviewing => _review.IsComparing;

    /// <summary>How far that is, e.g. "Conferindo os conjuntos que já existem… 3 de 12".</summary>
    public string ReviewingText => _review.ComparingText;

    /// <summary>How many sets the create will make, e.g. "12 conjuntos serão criados", or "2 de 12 conjuntos serão criados".</summary>
    public string SetsTitle => (Included.Count, Sets.Count) switch
    {
        (1, 1) => "1 conjunto será criado",
        (0, _) => "Nenhum conjunto será criado",
        var (included, all) when included == all => PtBrazil.Count(all, "conjunto", "conjuntos") + " serão criados",
        (1, var all) => $"1 de {all.ToString("N0", PtBrazil.Culture)} conjuntos será criado",
        var (included, all) => $"{included.ToString("N0", PtBrazil.Culture)} de {all.ToString("N0", PtBrazil.Culture)} conjuntos serão criados",
    };

    /// <summary>Where the recovery files will go: the single set's index file, or how many sets from which folder.</summary>
    public string? Destination => ShownSets switch
    {
        [] => null,
        [var only] => only.RecoveryFile,
        var sets => $"{PtBrazil.Count(sets.Count, "conjunto", "conjuntos")} em {Selection.BaseDirectory}",
    };

    /// <summary>The folder the recovery files go in, by name, or how many folders they go in.</summary>
    public string SaveIn => ShownSets.Select(set => set.BaseDirectory).Distinct(StringComparer.OrdinalIgnoreCase).ToList() switch
    {
        [] => "",
        [var folder] => Selection.FolderName(folder),
        var folders => PtBrazil.Count(folders.Count, "pasta", "pastas"),
    };

    // Where the header and the facts point: the sets included, or, with none included, every set.
    private IReadOnlyList<PlannedSet> ShownSets => Included is { Count: > 0 } included ? included : Sets;

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

    /// <summary>How many recovery files to make for each set, from 1 to <see cref="MaxRecoveryFiles"/>; null leaves it to the engine.</summary>
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

    /// <summary>What the engine says the create will make, every set together; null until it has said, or when it could not.</summary>
    public CreationEstimate? Estimate
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
                Announce(nameof(ParFilesWarning));
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

    /// <summary>What the estimate is at: "Calculando…", or, with several sets, how many are done.</summary>
    public string EstimatingText => Sets.Count > 1 ? $"Calculando… {_estimatedSets.ToString("N0", PtBrazil.Culture)} de {PtBrazil.Count(Sets.Count, "conjunto", "conjuntos")}" : "Calculando…";

    private int EstimatedSets
    {
        get => _estimatedSets;
        set
        {
            if (Set(ref _estimatedSets, value))
            {
                Announce(nameof(EstimatingText));
            }
        }
    }

    /// <summary>Why the sizes could not be worked out; null when they could.</summary>
    public string? EstimateProblem { get => _estimateProblem; private set => Set(ref _estimateProblem, value); }

    /// <summary>How much space the recovery files will take, every set together, e.g. "≈ 310 MB".</summary>
    public string SpaceText => Estimate is { } estimate ? "≈ " + SizeText.Of(estimate.TotalSize) : "—";

    /// <summary>
    /// The most lost data they can rebuild, e.g. "≈ 310 MB". With several sets, the share each can rebuild of
    /// its own files, e.g. "≈ 10% de cada conjunto": a set repairs only the damage in the files it protects.
    /// </summary>
    public string RepairableText => Estimate switch
    {
        null => "—",
        { Sets: [var only] } => "≈ " + SizeText.Of(only.RepairableSize),
        var estimate => $"≈ {PercentText(estimate.LeastRepairablePercent)} de cada conjunto",
    };

    /// <summary>The blocks the engine will use, e.g. "240 blocos de 24 KB, 24 para recuperação".</summary>
    public string BlocksText => Estimate switch
    {
        null => "",
        { Sets: [var only] } =>
            $"{PtBrazil.Count(only.SourceBlocks, "bloco", "blocos")} de {SizeText.Of(only.BlockSize)}, {only.RecoveryBlocks.ToString("N0", PtBrazil.Culture)} para recuperação",
        var estimate =>
            $"{PtBrazil.Count(estimate.SourceBlocks, "bloco", "blocos")} em {PtBrazil.Count(estimate.Sets.Count, "conjunto", "conjuntos")}, {estimate.RecoveryBlocks.ToString("N0", PtBrazil.Culture)} para recuperação",
    };

    /// <summary>How many recovery files the engine will make, the indexes aside, e.g. "1 arquivo de recuperação, além do índice".</summary>
    public string RecoveryFilesText => Estimate switch
    {
        null => "",
        var estimate => PtBrazil.Count(estimate.RecoveryFiles, "arquivo de recuperação", "arquivos de recuperação")
            + (estimate.Sets.Count == 1 ? ", além do índice" : $", além de {PtBrazil.Count(estimate.Sets.Count, "índice", "índices")}"),
    };

    /// <summary>In the per file mode, how many <c>.par2</c> files will appear among the files; otherwise null.</summary>
    public string? ParFilesWarning => Mode == CreationMode.PerFile && Estimate is { } estimate
        ? $"Vão aparecer {PtBrazil.Count(estimate.Par2Files, "arquivo .par2", "arquivos .par2")} nas pastas, ao lado dos arquivos protegidos."
        : null;

    /// <summary>How many of the sets included are already in their folders, so creating replaces them.</summary>
    public int ReplacedSets => Items.Count(item => item.IsIncluded && item.Comparison.Replaces);

    public bool ReplacesExisting => ReplacedSets > 0;

    public string CreateText => ReplacesExisting && ReplacedSets == Included.Count ? "Substituir arquivos de recuperação" : "Criar arquivos de recuperação";

    /// <summary>What happens to the sets being replaced; null when there are none.</summary>
    public string? ReplaceWarning
    {
        get
        {
            var (which, theyGo) = (ReplacedSets, Sets.Count) switch
            {
                (0, _) => (null, ""),
                (_, 1) => ("Já existe um conjunto com este nome nesta pasta.", "Ele só sai da pasta"),
                (1, _) => ("1 destes conjuntos já existe e será substituído.", "Ele só sai da pasta"),
                (var replaced, _) => ($"{replaced.ToString("N0", PtBrazil.Culture)} destes conjuntos já existem e serão substituídos.", "Cada um só sai da pasta"),
            };
            return which is null
                ? null
                : $"{which} {theyGo} depois que o novo ficar pronto"
                    + (HasRecycleBin ? ", e vai para a Lixeira." : ", e é apagado, porque este disco não tem Lixeira.");
        }
    }

    /// <summary>
    /// Only once every set is compared with its folder, and the engine has worked out the sizes of the ones
    /// included for the current settings, so what is shown is what is made.
    /// </summary>
    public bool CanCreate =>
        CanChoose && SelectionProblem is null && Included.Count > 0 && Estimate is not null && !IsEstimating && !IsReviewing;

    /// <summary>Which set is being made, when there are several, e.g. "Conjunto 3 de 12: 07 - Viagem Bahia"; otherwise null.</summary>
    public string? SetProgressText { get => _setProgressText; private set => Set(ref _setProgressText, value); }

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

    /// <summary>The engine is running, so it can be paused (not while a set it replaces is moved away).</summary>
    public bool CanControl => Run is not null;

    /// <summary>The create can be canceled: at any point, the sets already made staying.</summary>
    public bool CanCancel => IsRunning && !_cancelRequested;

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
        // With why sets already in the folders could not be read, if any could not.
        get => SetCreation.Join(_details, _review.Details);
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
    /// Whether the files of a set replaced go to the Recycle Bin: Windows keeps one only on fixed disks, and
    /// deletes at once on USB sticks, memory cards and network folders. Every set is on the drive chosen.
    /// </summary>
    private bool HasRecycleBin => Selection.BaseDirectory is { } folder && _hasRecycleBin(folder);

    private static bool DriveHasRecycleBin(string folder) =>
        !folder.StartsWith(@"\\", StringComparison.Ordinal) && new DriveInfo(folder).DriveType == DriveType.Fixed;

    /// <summary>
    /// Adds <paramref name="paths"/> (files or folders) to what was chosen, reading them from disk off the
    /// screen's thread, then asks the engine how big the sets will be.
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

        Selection read;
        try
        {
            read = await Task.Run(() => Selection.Read(all)).ConfigureAwait(true);
        }
        catch (Exception failure)
        {
            // Shown on screen with its details: a folder vanished mid-read, a disk failed...
            Stage = CreateStage.Failed;
            Banner = new Banner(BannerKind.Error, "Não foi possível ler os arquivos escolhidos.", "Confira se o disco está acessível e tente de novo.");
            Details = failure.ToString();
            return;
        }

        // Ready before the plan, so the plan's estimate may start.
        Stage = read.Roots.Count > 0 ? CreateStage.Ready : CreateStage.Idle;
        Selection = read;
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
        Banner = null;
        Details = null;
        Heading = "Criar";
        Stage = CreateStage.Idle;
    }

    /// <summary>
    /// Creates the sets, one after another; a set that fails stops the rest, and the sets already made stay.
    /// Ends when the last engine ends; any failure is shown on the screen (<see cref="Banner"/>,
    /// <see cref="Details"/>), not thrown. Only when <see cref="CanCreate"/>.
    /// </summary>
    public async Task CreateAsync()
    {
        if (!CanCreate)
        {
            throw new InvalidOperationException("Nothing to create.");
        }

        var sets = Included;
        var hasRecycleBin = HasRecycleBin;
        _cancelRequested = false;
        StopEstimate();
        Stage = CreateStage.Creating;
        Heading = "Criando";
        Banner = null;
        Details = null;

        var made = new List<(PlannedSet Set, SetResult Result)>();
        for (var index = 0; index < sets.Count; index++)
        {
            var set = sets[index];
            SetProgressText = sets.Count > 1 ? $"Conjunto {(index + 1).ToString("N0", PtBrazil.Culture)} de {sets.Count.ToString("N0", PtBrazil.Culture)}: {set.Name}" : null;
            ActivityText = "Preparando";
            Percent = null;
            IsPaused = false;

            var result = await CreateSetAsync(set, hasRecycleBin).ConfigureAwait(true);
            if (_disposed)
            {
                // The window closed: nothing is shown any more.
                return;
            }

            if (result.End != SetEnd.Created)
            {
                ShowStopped(sets, set, index, made.Count, result);
                return;
            }

            made.Add((set, result));
        }

        ShowCreated(made);
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

    /// <summary>Stops the create: the set under way is not made, nor the ones after it; the ones made stay.</summary>
    public void Cancel()
    {
        if (!IsRunning)
        {
            return;
        }

        _cancelRequested = true;
        Announce(nameof(CanCancel));
        Run?.Cancel();
    }

    /// <summary>
    /// Ends the engine at once if it is still running, e.g. when the window closes, and removes the recovery
    /// files it left half written: ended this way, the engine does not remove them itself.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        StopEstimate();
        _review.Stop();
        _creating?.Kill();
    }

    /// <summary>Makes one set: the engine writes it under a draft name, which replaces the set of the same name once made.</summary>
    private async Task<SetResult> CreateSetAsync(PlannedSet set, bool hasRecycleBin)
    {
        var runNumber = ++_runNumber;
        var creation = new SetCreation(CommandFor(set), hasRecycleBin, _startEngine, _recycle);
        _creating = creation;
        try
        {
            bool replaces;
            try
            {
                // Read again here, one folder listing: what the end says about the set there must be true now.
                replaces = SetCreation.Replaced(set).Count > 0;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                return new SetResult(SetEnd.Failed, null, failure.ToString())
                {
                    Title = "Não foi possível ler a pasta do conjunto.",
                    Hint = "Confira se o disco está acessível e tente de novo.",
                };
            }

            return await creation.CreateAsync(
                replaces,
                mayStart: () => !_disposed && !_cancelRequested,
                runChanged: run =>
                {
                    Run = run;
                    if (run is null)
                    {
                        IsPaused = false;
                    }
                    else
                    {
                        ActivityText = "Preparando";
                    }
                },
                activityChanged: activity => ActivityText = activity,
                new UiProgress<EngineProgress>(progress =>
                {
                    // An update posted just before the task ended may arrive after it: ignore it then.
                    if (runNumber == _runNumber && Run is not null)
                    {
                        Percent = progress.Percent;
                        ActivityText = ActivityTextOf(progress.Activity);
                    }
                })).ConfigureAwait(true);
        }
        finally
        {
            _creating = null;
        }
    }

    private void ShowCreated(List<(PlannedSet Set, SetResult Result)> made)
    {
        // The sets are made: nothing from here on may remove them, and a failure here only changes what is shown.
        Stage = CreateStage.Created;
        Heading = made.Count == 1 ? "Conjunto criado" : "Conjuntos criados";
        SetProgressText = null;
        _review.Start(Sets);

        var where = Selection.BaseDirectory;
        var results = made.Select(set => set.Result).ToList();
        var madeText = results.All(result => result.FilesSize is not null)
            ? $"{PtBrazil.Count(results.Sum(result => result.FileCount), "arquivo", "arquivos")} · {SizeText.Of(results.Sum(result => result.FilesSize ?? 0))}, em {where}"
            : $"Em {where}";
        var notes = results.Select(result => result.Note).OfType<string>().Distinct().Aggregate((string?)null, (all, note) => SetCreation.Join(all, note, " "));

        // What the sets made protect, which is less than what was chosen when some were left out.
        var files = PtBrazil.Count(made.Sum(set => set.Set.Files.Count), "arquivo", "arquivos") + " · " + SizeText.Of(made.Sum(set => set.Set.TotalSize));
        var protects = made.Count == 1 ? files : $"{files} em {PtBrazil.Count(made.Count, "conjunto", "conjuntos")}";
        Banner = new Banner(BannerKind.Success, $"Os arquivos de recuperação protegem {protects}.", SetCreation.Join(madeText, notes, " "));
        Details = results.Select(result => result.Details).OfType<string>().Aggregate((string?)null, (all, details) => SetCreation.Join(all, details));
    }

    /// <summary>Shows a create that stopped at <paramref name="set"/>, canceled or failed, the <paramref name="made"/> sets before it made.</summary>
    private void ShowStopped(IReadOnlyList<PlannedSet> sets, PlannedSet set, int index, int made, SetResult result)
    {
        SetProgressText = null;
        _review.Start(Sets);
        if (result.End == SetEnd.Canceled)
        {
            Stage = CreateStage.Canceled;
            Heading = "Criação cancelada";
            var title = made == 0
                ? "Nenhum arquivo de recuperação foi gravado."
                : $"{made.ToString("N0", PtBrazil.Culture)} de {PtBrazil.Count(sets.Count, "conjunto", "conjuntos")} {(made == 1 ? "ficou pronto" : "ficaram prontos")}; a criação foi cancelada.";
            Banner = new Banner(BannerKind.Warning, title, result.Note);
        }
        else
        {
            Stage = CreateStage.Failed;
            Heading = "Não foi possível criar";
            var where = sets.Count == 1
                ? null
                : $"Parou no conjunto \"{set.Name}\" ({(index + 1).ToString("N0", PtBrazil.Culture)} de {sets.Count.ToString("N0", PtBrazil.Culture)}); "
                    + made switch
                    {
                        0 => "nenhum ficou pronto.",
                        1 => "1 ficou pronto.",
                        var count => $"{count.ToString("N0", PtBrazil.Culture)} ficaram prontos.",
                    };
            Banner = new Banner(BannerKind.Error, result.Title ?? "Não foi possível criar.", SetCreation.Join(where, SetCreation.Join(result.Hint, result.Note, " "), " "));
        }

        Details = result.Details;
        RequestEstimate();
    }

    private void OnReviewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SetReview.Items):
                Announce(nameof(Items));
                break;
            case nameof(SetReview.Included):
                Announce(nameof(Included));
                Announce(nameof(SetsTitle));
                Announce(nameof(Destination));
                Announce(nameof(SaveIn));
                Announce(nameof(ReplacedSets));
                Announce(nameof(ReplacesExisting));
                Announce(nameof(CreateText));
                Announce(nameof(ReplaceWarning));
                ShowEstimateOfIncluded();
                Announce(nameof(CanCreate));
                break;
            case nameof(SetReview.IsComparing):
                Announce(nameof(IsReviewing));
                Announce(nameof(CanCreate));
                break;
            case nameof(SetReview.ComparingText):
                Announce(nameof(ReviewingText));
                break;
            case nameof(SetReview.Details):
                Announce(nameof(Details));
                break;
        }
    }

    /// <summary>The estimate of the sets included, from the engine's estimate of every set; null while there is none or none is included.</summary>
    private void ShowEstimateOfIncluded()
    {
        if (_setEstimates is not { } estimates || estimates.Length != Sets.Count)
        {
            Estimate = null;
            return;
        }

        var included = Items.Select((item, index) => (item.IsIncluded, Set: Sets[index], Estimate: estimates[index])).Where(set => set.IsIncluded).ToList();
        Estimate = included.Count == 0 ? null : new CreationEstimate([.. included.Select(set => set.Set)], [.. included.Select(set => set.Estimate)]);
    }

    private EngineCommand.Create CommandFor(PlannedSet set) =>
        new(set.RecoveryFile, set.BaseDirectory, set.Files, Redundancy, new CreateOptions(BlockSize.Value, RecoveryFileCount, Memory.Value));

    private void RequestEstimate()
    {
        _estimateNumber++;
        if (_disposed
            || Stage is not (CreateStage.Ready or CreateStage.Created or CreateStage.Canceled or CreateStage.Failed)
            || Sets.Count == 0 || SelectionProblem is not null)
        {
            // An answer under way is not wanted: what it was for has changed.
            _estimateAgain = false;
            _estimateStop?.Cancel();
            return;
        }

        // IsEstimating is set before the loop's first wait, so a round that ends at once is never mistaken for one under way.
        if (IsEstimating)
        {
            // Again and stopped, in this order: a trial that stops at once goes straight on to the next round.
            _estimateAgain = true;
            _estimateStop?.Cancel();
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
                var sets = Sets;
                using var stop = new CancellationTokenSource();
                _estimateStop = stop;
                try
                {
                    var estimates = await EstimateSetsAsync(sets, stop.Token).ConfigureAwait(true);
                    if (number == _estimateNumber)
                    {
                        var clearDetails = _detailsFromEstimate;
                        _setEstimates = estimates;
                        ShowEstimateOfIncluded();
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
                        _setEstimates = null;
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

    /// <summary>
    /// The engine's trial of each set. A few run at once: each is a short engine run that reads only the files'
    /// sizes. The first that fails stops the others, and its failure is thrown.
    /// </summary>
    private async Task<RecoveryEstimate[]> EstimateSetsAsync(IReadOnlyList<PlannedSet> sets, CancellationToken stop)
    {
        var estimates = new RecoveryEstimate[sets.Count];
        var next = 0;
        EstimatedSets = 0;
        Exception? firstFailure = null;
        using var round = CancellationTokenSource.CreateLinkedTokenSource(stop);

        // Several trials wait at once, but every step between the waits runs on the screen's thread
        // (ConfigureAwait(true)), one at a time: the shared counters need no locking.
        async Task EstimateNextAsync()
        {
            for (var index = next++; index < sets.Count && !round.IsCancellationRequested; index = next++)
            {
                try
                {
                    estimates[index] = await _runTrial(CommandFor(sets[index]), round.Token).ConfigureAwait(true);
                }
                catch (Exception failure) when (failure is not OperationCanceledException || !round.IsCancellationRequested)
                {
                    // Kept and thrown once the others have stopped; the screen shows it.
                    firstFailure ??= failure;
                    round.Cancel();
                    return;
                }

                EstimatedSets++;
            }
        }

        try
        {
            await Task.WhenAll(Enumerable.Range(0, Math.Min(sets.Count, Environment.ProcessorCount)).Select(_ => EstimateNextAsync())).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (round.IsCancellationRequested)
        {
            // The others, stopped: by a newer change (thrown below) or by the first failure (thrown below).
        }

        stop.ThrowIfCancellationRequested();
        if (firstFailure is not null)
        {
            ExceptionDispatchInfo.Throw(firstFailure);
        }

        return estimates;
    }

    private void StopEstimate()
    {
        _estimateNumber++;
        _estimateAgain = false;
        _estimateStop?.Cancel();
    }

    private static string PercentText(double percent) =>
        percent.ToString(percent < 10 ? "0.#" : "0", PtBrazil.Culture) + "%";

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
