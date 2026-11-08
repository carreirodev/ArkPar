// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
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

    // The estimate: one round at a time, a trial for each set included that has none yet. A settings change while one
    // runs stops it and asks for one more; a change in what is included only asks for one more.
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

    // The engine's estimate of each planned set it was asked about, by the trial asked (the set and the settings), for
    // the plan shown: a box ticked again, or a setting put back, needs no new trial. A new plan starts empty. The
    // answers come on the screen's thread (see EstimateSetsAsync); without one (the engine's own tests) two may land
    // at once, where a count off by one shows nothing wrong but a broken dictionary would, hence concurrent.
    private ConcurrentDictionary<EngineCommand.Create, RecoveryEstimate> _setEstimates = new();
    private bool _isEstimating;
    private int _estimatedSets;
    private int _estimatingSets;

    // The trials of the round under way.
    private HashSet<EngineCommand.Create> _roundTrials = [];

    // The trial whose failure is shown: it stands, not tried again, until a setting changes or its set is left out.
    private EngineCommand.Create? _failedTrial;
    private string? _estimateProblem;
    private string _heading = "Criar";
    private string? _setProgressText;
    private string _activityText = "";
    private double? _percent;
    private bool _isPaused;
    private Banner? _banner;
    private string? _details;
    private bool _isDeletingOrphanedSets;

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
                Announce(nameof(CanDeleteOrphanedSets));
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
                var plan = CreationPlan.For(value, Mode);
                if (ReferenceEquals(plan, Plan))
                {
                    // Still no set to make (CreationPlan.Empty), but the folders chosen changed, and their orphaned sets with them.
                    _review.Start(plan.Sets, value);
                }
                else
                {
                    Plan = plan;
                }
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
            Announce(nameof(HasSetList));
            Announce(nameof(SelectionProblem));
            Announce(nameof(SaveIn));
            Announce(nameof(Destination));
            Announce(nameof(EstimatingText));
            Announce(nameof(ParFilesWarning));
            StopEstimate();
            _setEstimates = new();
            Estimate = null;
            EstimateProblem = null;
            _review.Start(value.Sets, Selection);
            EstimateIncluded();
        }
    }

    /// <summary>The sets the mode splits what was chosen into, each with where it goes.</summary>
    public IReadOnlyList<PlannedSet> Sets => Plan.Sets;

    public bool HasSets => Sets.Count > 0;

    /// <summary>Whether the list of sets shows: there are sets to make, or orphaned sets to warn about.</summary>
    public bool HasSetList => HasSets || HasOrphanedSets;

    /// <summary>
    /// One line for each of <see cref="Sets"/>: what is in its folder now, and whether the create makes it. A set
    /// up to date starts left out.
    /// </summary>
    public IReadOnlyList<ReviewedSet> Items => _review.Items;

    /// <summary>
    /// The orphaned sets in the folders chosen (GLOSSARY.md), each a warning below <see cref="Items"/>: the create
    /// never makes them. Found after the sets are compared, so they may still be appearing once Criar is enabled.
    /// </summary>
    public IReadOnlyList<OrphanedSet> OrphanedSets => _review.OrphanedSets;

    public bool HasOrphanedSets => OrphanedSets.Count > 0;

    /// <summary>The engine is still listing the other sets in the folders chosen, to find the orphaned ones.</summary>
    public bool IsFindingOrphanedSets => _review.IsFindingOrphanedSets;

    /// <summary>Whether the orphaned sets can be deleted now: not while reading, creating or deleting.</summary>
    public bool CanDeleteOrphanedSets => CanChoose && !_isDeletingOrphanedSets;

    /// <summary>"Apagar os 200 conjuntos", when more than one orphaned set can be deleted; otherwise null.</summary>
    public string? DeleteAllText => OrphanedSets.Count(set => set.CanDelete) is var count and > 1
        ? $"Apagar os {count.ToString("N0", PtBrazil.Culture)} conjuntos"
        : null;

    /// <summary>
    /// What to ask before deleting the sets of <paramref name="rows"/>, title and text: when some still protect files on the
    /// disk, which are left without them, or when the drive has no Recycle Bin, so they go for good. Null when
    /// they protect nothing and go to the Recycle Bin.
    /// </summary>
    public (string Title, string Text)? DeleteQuestion(IReadOnlyList<OrphanedSet> rows)
    {
        // A folder row is not deleted, so it is not counted.
        List<OrphanedSet> sets = [.. rows.Where(set => set.CanDelete)];
        var hasRecycleBin = HasRecycleBin;
        var protecting = sets.Where(set => set.StillProtected > 0).ToList();
        if (protecting.Count == 0 && hasRecycleBin)
        {
            return null;
        }

        var stillThere = protecting.Sum(set => set.StillProtected);
        var (files, theyStay) = stillThere == 1
            ? ("1 arquivo que está no disco", "ele fica")
            : ($"{stillThere.ToString("N0", PtBrazil.Culture)} arquivos que estão no disco", "eles ficam");
        var protects = (sets, protecting.Count) switch
        {
            (_, 0) => null,
            ([var only], _) => $"{only.Name} ainda protege {files}, e {theyStay} sem esse conjunto.",
            (_, 1) => $"Entre eles, 1 ainda protege {files}, e {theyStay} sem ele.",
            (_, var count) => $"Entre eles, {count.ToString("N0", PtBrazil.Culture)} ainda protegem {files}, e {theyStay} sem eles.",
        };
        var goes = (hasRecycleBin, sets) switch
        {
            (true, [_]) => "Ele vai para a Lixeira.",
            (true, _) => "Eles vão para a Lixeira.",
            (false, [var only]) => $"Este disco não tem Lixeira: {only.Name} será apagado de vez, sem como recuperar.",
            (false, _) => $"Este disco não tem Lixeira: os {sets.Count.ToString("N0", PtBrazil.Culture)} conjuntos serão apagados de vez, sem como recuperar.",
        };
        var title = (hasRecycleBin, sets) switch
        {
            (false, _) => "Apagar de vez?",
            (true, [_]) => "Apagar o conjunto?",
            _ => "Apagar os conjuntos?",
        };
        return (title, SetCreation.Join(protects, goes, " ") ?? goes);
    }

    /// <summary>
    /// Deletes the files of <paramref name="sets"/> (index and recovery files), to the Recycle Bin when the drive
    /// has one, and takes them off <see cref="OrphanedSets"/>. A set that cannot be deleted stays, and the
    /// <see cref="Banner"/> says so, with the reason in <see cref="Details"/>. Only when <see cref="CanDeleteOrphanedSets"/>.
    /// </summary>
    public async Task DeleteOrphanedSetsAsync(IReadOnlyList<OrphanedSet> sets)
    {
        if (!CanDeleteOrphanedSets)
        {
            throw new InvalidOperationException("The orphaned sets cannot be deleted now.");
        }

        var hasRecycleBin = HasRecycleBin;
        IsDeletingOrphanedSets = true;
        try
        {
            var deleted = new List<OrphanedSet>();
            var failures = new List<string>();
            foreach (var set in sets.Where(set => set.CanDelete))
            {
                try
                {
                    await Task.Run(() => DeleteSet(set.Path, hasRecycleBin)).ConfigureAwait(true);
                    deleted.Add(set);
                }
                catch (Exception failure)
                {
                    // Shown with its details: a file held by another program, or its removal refused in Windows' dialog.
                    failures.Add($"{set.Path}:\n{failure}");
                }
            }

            _review.Remove(deleted);
            if (failures.Count > 0)
            {
                Banner = new Banner(
                    BannerKind.Warning,
                    failures.Count == 1 ? "1 conjunto não pôde ser apagado." : $"{failures.Count.ToString("N0", PtBrazil.Culture)} conjuntos não puderam ser apagados.",
                    (failures.Count == 1 ? "Algum arquivo dele" : "Algum arquivo deles") + " pode estar aberto em outro programa. O motivo está em Detalhes técnicos.");
                Details = failures.Aggregate((string?)null, (all, failure) => SetCreation.Join(all, failure));
            }
        }
        finally
        {
            IsDeletingOrphanedSets = false;
        }
    }

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

    /// <summary>What the estimate is at: "Calculando…", or, with several sets to work out, how many are done.</summary>
    public string EstimatingText => _estimatingSets > 1
        ? $"Calculando… {_estimatedSets.ToString("N0", PtBrazil.Culture)} de {PtBrazil.Count(_estimatingSets, "conjunto", "conjuntos")}"
        : "Calculando…";

    // How many sets the round under way works out.
    private int EstimatingSets
    {
        get => _estimatingSets;
        set
        {
            if (Set(ref _estimatingSets, value))
            {
                Announce(nameof(EstimatingText));
            }
        }
    }

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

    private bool IsDeletingOrphanedSets
    {
        get => _isDeletingOrphanedSets;
        set
        {
            if (Set(ref _isDeletingOrphanedSets, value))
            {
                Announce(nameof(CanDeleteOrphanedSets));
            }
        }
    }

    /// <summary>Deletes the files of the set whose index is <paramref name="recoveryFile"/>, as a set replaced is.</summary>
    private void DeleteSet(string recoveryFile, bool hasRecycleBin)
    {
        var files = Selection.RecoverySetFiles(recoveryFile);
        if (hasRecycleBin)
        {
            if (files.Count > 0)
            {
                _recycle(files);
            }

            return;
        }

        foreach (var file in files)
        {
            File.Delete(file);
        }
    }

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

        // The orphaned sets still being looked for: the engine is the create's now, and the review starts again after it.
        _review.Stop();
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
                AnnounceFinished();
                return;
            }

            made.Add((set, result));
        }

        ShowCreated(made);
        AnnounceFinished();
    }

    /// <summary>
    /// A create ended, with the <see cref="Banner"/> the screen now shows (so the app can tell a user who is looking
    /// elsewhere). Once for all the sets, not once per set.
    /// </summary>
    public event EventHandler<Banner>? Finished;

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
        _review.Start(Sets, Selection);

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

    private void AnnounceFinished()
    {
        if (Banner is { } banner)
        {
            Finished?.Invoke(this, banner);
        }
    }

    /// <summary>Shows a create that stopped at <paramref name="set"/>, canceled or failed, the <paramref name="made"/> sets before it made.</summary>
    private void ShowStopped(IReadOnlyList<PlannedSet> sets, PlannedSet set, int index, int made, SetResult result)
    {
        SetProgressText = null;
        _review.Start(Sets, Selection);
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

        // A create that stopped may have been stopped by the files themselves (one gone, the disk full): the earlier
        // estimates are not trusted, and every set included is worked out again.
        _setEstimates = new();
        RequestEstimate();
    }

    private void OnReviewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SetReview.Items):
                Announce(nameof(Items));
                break;
            case nameof(SetReview.OrphanedSets):
                Announce(nameof(OrphanedSets));
                Announce(nameof(HasOrphanedSets));
                Announce(nameof(HasSetList));
                Announce(nameof(DeleteAllText));
                break;
            case nameof(SetReview.IsFindingOrphanedSets):
                Announce(nameof(IsFindingOrphanedSets));
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

                // A set just included, or just compared, may have no estimate yet; one left out no longer needs its own.
                EstimateIncluded();
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

    /// <summary>The estimate of the sets included, for the current settings; null while one of them has none, or none is included.</summary>
    private void ShowEstimateOfIncluded()
    {
        var included = Included;
        var estimates = new List<RecoveryEstimate>(included.Count);
        foreach (var set in included)
        {
            if (!_setEstimates.TryGetValue(CommandFor(set), out var estimate))
            {
                Estimate = null;
                return;
            }

            estimates.Add(estimate);
        }

        Estimate = included.Count == 0 ? null : new CreationEstimate(included, estimates);
    }

    private EngineCommand.Create CommandFor(PlannedSet set) =>
        new(set.RecoveryFile, set.BaseDirectory, set.Files, Redundancy, new CreateOptions(BlockSize.Value, RecoveryFileCount, Memory.Value));

    /// <summary>A setting changed, or a create stopped: the answers under way are not wanted, and a new round follows.</summary>
    private void RequestEstimate()
    {
        if (!MayEstimate)
        {
            // An answer under way is not wanted: what it was for has changed.
            StopEstimate();
            return;
        }

        _estimateNumber++;
        _failedTrial = null;

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

    /// <summary>
    /// What is included changed: the round under way goes on, its answers still hold, and one more follows for the
    /// sets it does not cover. A set left out is not asked about, so its failure no longer stops the others.
    /// </summary>
    private void EstimateIncluded()
    {
        if (!MayEstimate)
        {
            StopEstimate();
            return;
        }

        if (IsEstimating)
        {
            var wanted = WantedTrials().ToHashSet();
            if (_roundTrials.Any(trial => !wanted.Contains(trial) && !_setEstimates.ContainsKey(trial)))
            {
                // The round waits on a set just left out: its answer, or its failure, is not wanted. The answers
                // already in stay. Again and stopped, in this order, as in RequestEstimate.
                _estimateAgain = true;
                _estimateStop?.Cancel();
            }
            else if (wanted.Any(trial => !_roundTrials.Contains(trial) && !_setEstimates.ContainsKey(trial)))
            {
                _estimateAgain = true;
            }

            return;
        }

        // The loop shows every failure on the screen itself.
        _ = EstimateAsync();
    }

    /// <summary>
    /// The trial of each set included and compared: one still being compared may turn out up to date, and be left out.
    /// </summary>
    private IEnumerable<EngineCommand.Create> WantedTrials() =>
        Items.Where(item => item.IsIncluded && item.IsCompared).Select(item => CommandFor(item.Planned));

    private bool MayEstimate =>
        !_disposed
        && Stage is (CreateStage.Ready or CreateStage.Created or CreateStage.Canceled or CreateStage.Failed)
        && Sets.Count > 0 && SelectionProblem is null;

    private async Task EstimateAsync()
    {
        IsEstimating = true;
        try
        {
            do
            {
                _estimateAgain = false;
                if (_failedTrial is { } failed && WantedTrials().Contains(failed))
                {
                    // The failure shown still stands: asking again would only repeat it.
                    break;
                }

                var number = _estimateNumber;
                var estimates = _setEstimates;
                var trials = WantedTrials().Where(trial => !estimates.ContainsKey(trial)).ToList();
                _roundTrials = [.. trials];
                using var stop = new CancellationTokenSource();
                _estimateStop = stop;
                try
                {
                    var answer = await EstimateSetsAsync(trials, estimates, stop.Token).ConfigureAwait(true);
                    if (number != _estimateNumber)
                    {
                        continue;
                    }

                    _failedTrial = answer?.Trial;
                    if (answer is { Failure: var failure })
                    {
                        ShowEstimateFailure(failure);
                    }
                    else
                    {
                        var clearDetails = _detailsFromEstimate;
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
                    // Stopped because a setting or what was chosen changed: its answer is not wanted, but a newer one may be.
                    continue;
                }
                catch (Exception failure)
                {
                    // Not a trial's: shown all the same, with its details.
                    if (number == _estimateNumber)
                    {
                        ShowEstimateFailure(failure);
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
            _roundTrials = [];
            IsEstimating = false;
        }
    }

    private void ShowEstimateFailure(Exception failure)
    {
        Estimate = null;
        EstimateProblem = "Não foi possível calcular o tamanho com estes ajustes.";
        Details = failure is EngineTrialException trial ? trial.Output : failure.ToString();
        _detailsFromEstimate = true;
    }

    /// <summary>
    /// Runs the engine's <paramref name="trials"/>, keeping each answer in <paramref name="estimates"/> as it comes.
    /// A few run at once: each is a short engine run that reads only the files' sizes. The first that fails stops
    /// the others and is returned; the answers already in stay. Null when every trial answered.
    /// </summary>
    private async Task<(EngineCommand.Create Trial, Exception Failure)?> EstimateSetsAsync(
        List<EngineCommand.Create> trials,
        ConcurrentDictionary<EngineCommand.Create, RecoveryEstimate> estimates,
        CancellationToken stop)
    {
        var next = 0;
        EstimatingSets = trials.Count;
        EstimatedSets = 0;
        (EngineCommand.Create Trial, Exception Failure)? firstFailure = null;
        using var round = CancellationTokenSource.CreateLinkedTokenSource(stop);

        // Several trials wait at once, but every step between the waits runs on the screen's thread
        // (ConfigureAwait(true)), one at a time: the shared counters need no locking.
        async Task EstimateNextAsync()
        {
            for (var index = next++; index < trials.Count && !round.IsCancellationRequested; index = next++)
            {
                try
                {
                    estimates[trials[index]] = await _runTrial(trials[index], round.Token).ConfigureAwait(true);
                }
                catch (Exception failure) when (failure is not OperationCanceledException || !round.IsCancellationRequested)
                {
                    // Kept and returned once the others have stopped; the screen shows it.
                    firstFailure ??= (trials[index], failure);
                    round.Cancel();
                    return;
                }

                EstimatedSets++;
            }
        }

        try
        {
            await Task.WhenAll(Enumerable.Range(0, Math.Min(trials.Count, Environment.ProcessorCount)).Select(_ => EstimateNextAsync())).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (round.IsCancellationRequested)
        {
            // The others, stopped: by a newer change (thrown below) or by the first failure (returned below).
        }

        stop.ThrowIfCancellationRequested();
        return firstFailure;
    }

    private void StopEstimate()
    {
        _estimateNumber++;
        _failedTrial = null;
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
