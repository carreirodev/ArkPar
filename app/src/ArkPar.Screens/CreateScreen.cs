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

    /// <summary>Added to the names of a replaced set while it waits aside (on a drive without a Recycle Bin).</summary>
    private const string AsideSuffix = ".anterior";

    /// <summary>How long a killed engine gets to let go of its files before the partial ones are removed.</summary>
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    private readonly Func<EngineCommand, IProgress<EngineProgress>, IEngineRun> _startEngine;
    private readonly Func<EngineCommand.Create, CancellationToken, Task<RecoveryEstimate>> _runTrial;
    private readonly Action<IReadOnlyList<string>> _recycle;
    private readonly Func<string, bool> _hasRecycleBin;
    private IEngineRun? _run;
    private EngineCommand.Create? _creating;
    private int _runNumber;
    private bool _disposed;

    // What the create under way did with the set it replaces: sent it to the Recycle Bin, or set it aside.
    private bool _replacedToRecycleBin;
    private List<(string Original, string Aside)>? _setAside;

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
    /// <param name="recycle">Sends files to the Recycle Bin.</param>
    /// <param name="hasRecycleBin">Whether a folder's drive has a Recycle Bin; by default, only fixed disks do.</param>
    public CreateScreen(
        Func<EngineCommand, IProgress<EngineProgress>, IEngineRun> startEngine,
        Func<EngineCommand.Create, CancellationToken, Task<RecoveryEstimate>> runTrial,
        Action<IReadOnlyList<string>> recycle,
        Func<string, bool>? hasRecycleBin = null)
    {
        _startEngine = startEngine;
        _runTrial = runTrial;
        _recycle = recycle;
        _hasRecycleBin = hasRecycleBin ?? DriveHasRecycleBin;
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
            + (HasRecycleBin
                ? "Os arquivos dele irão para a Lixeira."
                : "Ele fica guardado ao lado até o novo ficar pronto, e então é apagado, porque este disco não tem Lixeira.");

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
    private bool HasRecycleBin => Selection.BaseDirectory is { } folder && _hasRecycleBin(folder);

    private static bool DriveHasRecycleBin(string folder) =>
        !folder.StartsWith(@"\\", StringComparison.Ordinal) && new DriveInfo(folder).DriveType == DriveType.Fixed;

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
        _replacedToRecycleBin = false;
        _setAside = null;
        StopEstimate();
        Stage = CreateStage.Creating;
        Heading = "Criando";
        ActivityText = replaced.Count == 0 ? "Preparando"
            : hasRecycleBin ? "Levando o conjunto anterior para a Lixeira"
            : "Guardando o conjunto anterior";
        Percent = null;
        IsPaused = false;
        Banner = null;
        Details = null;

        try
        {
            // The engine would write over the index but leave the replaced set's recovery files beside the new ones.
            if (replaced.Count > 0 && hasRecycleBin)
            {
                await Task.Run(() => _recycle(replaced)).ConfigureAwait(true);
                _replacedToRecycleBin = true;
            }
            else if (replaced.Count > 0)
            {
                // No Recycle Bin on this drive: the set waits aside, so a create that does not finish loses nothing.
                _setAside = await Task.Run(() => SetAside(replaced)).ConfigureAwait(true);
            }

            if (_disposed)
            {
                // The window closed meanwhile: start nothing that would outlive it, and give the set back.
                RestoreSetAside();
                return;
            }

            ActivityText = "Preparando";

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
            var movingAway = replaced.Count > 0 && !_replacedToRecycleBin && _setAside is null;
            var (note, settleFailure) = await SettleReplacedSetAsync(created: false).ConfigureAwait(true);
            Fail(movingAway
                    ? new Banner(BannerKind.Error, "Não foi possível tirar o conjunto anterior da pasta.", "Confira se algum dos arquivos dele está aberto em outro programa.")
                    : new Banner(BannerKind.Error, "Não foi possível iniciar a criação.", Join("O ArkPar pode estar instalado de forma incompleta.", note, " ")),
                Join(failure.ToString(), settleFailure));
            return;
        }

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
            var (note, settleFailure) = await SettleReplacedSetAsync(created: false).ConfigureAwait(true);
            Fail(new Banner(BannerKind.Error, "A criação terminou de forma inesperada.", Join(note, LeftoverText(create, leftover), " ")),
                Join(Join(failure.ToString(), leftover), settleFailure));
            return;
        }

        EndRun();
        await ShowAsync(result, create).ConfigureAwait(true);
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
            RestoreSetAside();
        }
    }

    private void EndRun()
    {
        Run?.Dispose();
        Run = null;
        _creating = null;
        IsPaused = false;
    }

    private async Task ShowAsync(EngineResult result, EngineCommand.Create create)
    {
        if (result.Outcome == EngineOutcome.Completed)
        {
            // The set is made: nothing from here on may remove it, and a failure here only changes what is shown.
            Stage = CreateStage.Created;
            Heading = "Conjunto criado";
            var (note, settleFailure) = await SettleReplacedSetAsync(created: true).ConfigureAwait(true);
            CheckForExistingSet();
            string? made;
            string? sizeFailure = null;
            try
            {
                var recoveryFiles = Selection.RecoverySetFiles(create.RecoveryFile);
                var size = await Task.Run(() => recoveryFiles.Sum(file => new FileInfo(file).Length)).ConfigureAwait(true);
                made = $"{PtBrazil.Count(recoveryFiles.Count, "arquivo", "arquivos")} · {SizeText.Of(size)}, em {create.BaseDirectory}";
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // A recovery file moved or locked right after it was made (an antivirus...): the set is still there.
                made = $"Em {create.BaseDirectory}";
                sizeFailure = failure.ToString();
            }

            Banner = new Banner(BannerKind.Success, $"Os arquivos de recuperação protegem {Selection.Summary}.", Join(made, note, " "));
            Details = Join(sizeFailure, settleFailure);
            return;
        }

        var leftover = await RemovePartialFilesAsync(create).ConfigureAwait(true);
        var (replacedNote, restoreFailure) = await SettleReplacedSetAsync(created: false).ConfigureAwait(true);
        if (result.Outcome == EngineOutcome.Canceled)
        {
            Stage = CreateStage.Canceled;
            Heading = "Criação cancelada";
            CheckForExistingSet();
            Banner = new Banner(BannerKind.Warning, "Nenhum arquivo de recuperação foi gravado.", Join(replacedNote, LeftoverText(create, leftover), " "));
            Details = Join(leftover, restoreFailure);
            RequestEstimate();
            return;
        }

        Fail(
            new Banner(BannerKind.Error, "O motor não conseguiu criar os arquivos de recuperação.",
                Join(Join("Confira se há espaço no disco e se a pasta permite gravar.", replacedNote, " "), LeftoverText(create, leftover), " ")),
            Join(Join(result.Output, leftover), restoreFailure));
    }

    /// <summary>
    /// Ends what this create did with the set it replaced. Made: the set waiting aside is deleted. Not made: it is
    /// given back, or, if it went to the Recycle Bin, the user is told it is there.
    /// </summary>
    /// <returns>What to tell the user about that set, and why a step failed, if one did.</returns>
    private async Task<(string? Note, string? Failure)> SettleReplacedSetAsync(bool created)
    {
        if (_setAside is null)
        {
            return (_replacedToRecycleBin && !created ? "O conjunto anterior está na Lixeira, se quiser recuperá-lo." : null, null);
        }

        try
        {
            await Task.Run(created ? DeleteSetAside : RestoreSetAside).ConfigureAwait(true);
            return (created ? null : "O conjunto anterior continua na pasta.", null);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Said on screen: the set is still on disk, only under another name.
            return (created
                    ? $"O conjunto anterior ficou na pasta com \"{AsideSuffix}\" no fim do nome; pode apagá-lo."
                    : $"O conjunto anterior ficou com \"{AsideSuffix}\" no fim do nome; tire esse final para usá-lo de novo.",
                failure.ToString());
        }
    }

    /// <summary>Renames <paramref name="files"/> aside, all or none: a move that fails puts back the ones already moved.</summary>
    private static List<(string Original, string Aside)> SetAside(IReadOnlyList<string> files)
    {
        var moved = new List<(string Original, string Aside)>();
        try
        {
            foreach (var file in files)
            {
                // Overwrites only what a create that never finished (the app closed hard) left aside before.
                File.Move(file, file + AsideSuffix, overwrite: true);
                moved.Add((file, file + AsideSuffix));
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            foreach (var (original, aside) in moved)
            {
                File.Move(aside, original);
            }

            throw;
        }

        return moved;
    }

    /// <summary>Gives the set waiting aside its names back (the half-written new one is already gone).</summary>
    private void RestoreSetAside()
    {
        if (_setAside is not { } aside)
        {
            return;
        }

        _setAside = null;
        foreach (var (original, asideName) in aside)
        {
            File.Move(asideName, original);
        }
    }

    private void DeleteSetAside()
    {
        if (_setAside is not { } aside)
        {
            return;
        }

        _setAside = null;
        foreach (var (_, asideName) in aside)
        {
            File.Delete(asideName);
        }
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
            DeleteOnceReleased(file);
        }
    }

    /// <summary>
    /// Deletes <paramref name="file"/>, waiting up to about a second for Windows to let go of it: right after
    /// the engine is ended at once, a file it was writing can still be held for a moment ("access denied").
    /// </summary>
    private static void DeleteOnceReleased(string file)
    {
        const int Attempts = 20;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Delete(file);
                return;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException && attempt < Attempts)
            {
                Thread.Sleep(50);
            }
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
