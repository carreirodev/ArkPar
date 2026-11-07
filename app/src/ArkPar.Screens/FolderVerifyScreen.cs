// SPDX-License-Identifier: GPL-2.0-or-later

using System.ComponentModel;
using ArkPar.Engine;

namespace ArkPar.Screens;

/// <summary>Where a folder verification is.</summary>
public enum FolderVerifyStage
{
    /// <summary>No folder chosen: the Verify and repair screen shows a single set.</summary>
    Idle,
    Searching,

    /// <summary>The sets found are listed, waiting for the user to start.</summary>
    Found,
    Verifying,
    Repairing,

    /// <summary>The summary is shown: after the verification, or after repairing.</summary>
    Done,
}

/// <summary>How many folders are in each group of the summary.</summary>
public sealed record FolderCounts(int Complete, int Repairable, int Unrepairable, int Unverified);

/// <summary>
/// A folder verification on the Verify and repair screen: finds every recovery set inside a folder, verifies them
/// one after another, and sums them up by folder. Each set's detail, and its repair, is the Verify and repair
/// screen itself (<see cref="Follow"/>). Use it from the screen's thread; the engine's progress is brought back to it.
/// </summary>
public sealed class FolderVerifyScreen : Observable, IDisposable
{
    private readonly Func<EngineCommand, IProgress<EngineProgress>, IEngineRun> _startEngine;
    private readonly Func<string, CancellationToken, FoundSets> _findSets;
    private VerifyScreen? _detail;
    private IEngineRun? _run;
    private int _runNumber;
    private CancellationTokenSource? _stopSearch;
    private bool _cancelRequested;
    private bool _disposed;

    private FolderVerifyStage _stage;
    private string? _folder;
    private string _heading = "";
    private IReadOnlyList<CheckedFolder> _folders = [];
    private IReadOnlyList<CheckedSet> _sets = [];
    private string _foundText = "";
    private string? _progressText;
    private string _activityText = "";
    private double? _percent;
    private bool _isPaused;
    private Banner? _banner;
    private FolderCounts? _counts;
    private string? _details;
    private CheckedSet? _openedSet;

    /// <param name="startEngine">Starts an engine task (<see cref="EngineRun.Start"/> in the app).</param>
    /// <param name="findSets">Finds the sets inside a folder; <see cref="SetSearch.Find"/> by default.</param>
    public FolderVerifyScreen(Func<EngineCommand, IProgress<EngineProgress>, IEngineRun> startEngine, Func<string, CancellationToken, FoundSets>? findSets = null)
    {
        _startEngine = startEngine;
        _findSets = findSets ?? SetSearch.Find;
    }

    public FolderVerifyStage Stage
    {
        get => _stage;
        private set
        {
            if (Set(ref _stage, value))
            {
                Announce(nameof(IsActive));
                Announce(nameof(IsRunning));
                Announce(nameof(IsSummaryShown));
                Announce(nameof(CanPause));
                Announce(nameof(CanCancel));
                AnnounceAbilities();
            }
        }
    }

    /// <summary>An engine task runs: it can be paused (a search cannot).</summary>
    public bool CanPause => Stage is FolderVerifyStage.Verifying or FolderVerifyStage.Repairing;

    /// <summary>Searching, verifying or repairing: it can be canceled.</summary>
    public bool CanCancel => IsRunning;

    /// <summary>A folder is chosen: the screen shows the folder verification, not a single set.</summary>
    public bool IsActive => Stage != FolderVerifyStage.Idle;

    /// <summary>Searching, verifying or repairing: nothing else may start.</summary>
    public bool IsRunning => Stage is FolderVerifyStage.Searching or FolderVerifyStage.Verifying or FolderVerifyStage.Repairing;

    /// <summary>The folder verification is on screen, not a set's detail.</summary>
    public bool IsSummaryShown => IsActive && OpenedSet is null;

    /// <summary>The folder chosen, in full.</summary>
    public string? Folder { get => _folder; private set => Set(ref _folder, value); }

    public string Heading { get => _heading; private set => Set(ref _heading, value); }

    /// <summary>The folders holding sets, in the order they are verified: each folder's own sets, then its subfolders'.</summary>
    public IReadOnlyList<CheckedFolder> Folders { get => _folders; private set => Set(ref _folders, value); }

    /// <summary>How many sets were found and where, e.g. "12 conjuntos em 9 pastas".</summary>
    public string FoundText { get => _foundText; private set => Set(ref _foundText, value); }

    /// <summary>Which set is under way, e.g. "Conjunto 3 de 12: 2019\Natal\Natal.par2"; null when nothing runs.</summary>
    public string? ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

    /// <summary>What the engine is doing right now, e.g. "Lendo praia 01.jpg".</summary>
    public string ActivityText { get => _activityText; private set => Set(ref _activityText, value); }

    /// <summary>How far the set under way is, from 0 to 100; null before the engine reports any.</summary>
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

    /// <summary>The headline: what was found, or the summary once verified.</summary>
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

    /// <summary>How many folders are in each group; null until the verification starts.</summary>
    public FolderCounts? Counts { get => _counts; private set => Set(ref _counts, value); }

    /// <summary>What went wrong, in technical terms (folders that could not be read...); otherwise null.</summary>
    public string? Details { get => _details; private set => Set(ref _details, value); }

    /// <summary>The set whose detail the Verify and repair screen shows; null while the summary is shown.</summary>
    public CheckedSet? OpenedSet
    {
        get => _openedSet;
        private set
        {
            if (Set(ref _openedSet, value))
            {
                Announce(nameof(IsSummaryShown));
            }
        }
    }

    /// <summary>Some sets were found, and nothing runs: the verification can start.</summary>
    public bool CanStart => Stage == FolderVerifyStage.Found && _sets.Count > 0;

    /// <summary>A set's detail can be opened: nothing runs, here or in the detail.</summary>
    public bool CanOpen => Stage == FolderVerifyStage.Done && _detail?.IsRunning != true;

    /// <summary>Some sets can be repaired, and nothing runs.</summary>
    public bool CanRepairAll => CanOpen && _sets.Any(set => set.Health == SetHealth.Repairable);

    /// <summary>
    /// The Verify and repair screen, where each set's detail opens: a repair or a new verification there updates
    /// the set here, and nothing starts here while it runs.
    /// </summary>
    public void Follow(VerifyScreen detail)
    {
        _detail = detail;
        detail.Ended += OnDetailEnded;
        detail.PropertyChanged += OnDetailChanged;
    }

    /// <summary>
    /// Finds the sets inside <paramref name="folder"/> and lists them, without verifying them yet. Ends when the
    /// search ends; any failure is shown on the screen, not thrown.
    /// </summary>
    public async Task ChooseAsync(string folder)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("A task is already running.");
        }

        Folder = folder;
        OpenedSet = null;
        Heading = "Procurando conjuntos";
        ActivityText = "Procurando arquivos .par2 nas pastas";
        Percent = null;
        Banner = null;
        Details = null;
        Counts = null;
        ShowSets([], []);
        Stage = FolderVerifyStage.Searching;

        using var stop = new CancellationTokenSource();
        _stopSearch = stop;
        FoundSets found;
        try
        {
            var findSets = _findSets;
            found = await Task.Run(() => findSets(folder, stop.Token), stop.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Canceled by the user (or the window closing): back to where the screen was before the folder.
            if (!_disposed)
            {
                Reset();
            }

            return;
        }
        catch (Exception failure)
        {
            // Shown on screen with its details: the app has nothing better to do with it.
            ShowUnreadableFolder(failure.ToString());
            return;
        }
        finally
        {
            _stopSearch = null;
        }

        if (_disposed)
        {
            return;
        }

        var leftOutDetails = "Pastas que ficaram de fora:\n" + string.Join("\n", found.LeftOut.Select(DescribeLeftOut));
        var chosen = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (found.LeftOut.Any(leftOut => leftOut.Path.Equals(chosen, StringComparison.OrdinalIgnoreCase)))
        {
            // The folder chosen itself: nothing inside it was searched, so "no set found" would not be true.
            ShowUnreadableFolder(leftOutDetails);
            return;
        }

        ShowSets(found.Sets, found.LeftOut);
        Heading = "Verificar uma pasta";
        FoundText = _sets.Count == 0
            ? ""
            : PtBrazil.Count(_sets.Count, "conjunto", "conjuntos") + " em " + PtBrazil.Count(Folders.Count(folder => folder.LeftOut is null), "pasta", "pastas");

        // A folder left out may hold sets: never say there is none, or later that all is intact, as if it were read.
        var leftOutCount = found.LeftOut.Count;
        var leftOutText = leftOutCount.ToString("N0", PtBrazil.Culture);
        Banner = (_sets.Count, leftOutCount) switch
        {
            (0, 0) => new Banner(BannerKind.Warning, "Nenhum conjunto de recuperação foi encontrado nesta pasta.", "Os conjuntos são os arquivos .par2 feitos na tela Criar."),
            (0, 1) => new Banner(BannerKind.Warning, "Nenhum conjunto foi encontrado nas pastas que puderam ser abertas.", "1 pasta ficou de fora e aparece na lista como não verificada."),
            (0, _) => new Banner(BannerKind.Warning, "Nenhum conjunto foi encontrado nas pastas que puderam ser abertas.", $"{leftOutText} pastas ficaram de fora e aparecem na lista como não verificadas."),
            (_, 0) => null,
            (_, 1) => new Banner(BannerKind.Warning, "1 pasta ficou de fora.", "Ela aparece na lista como não verificada."),
            _ => new Banner(BannerKind.Warning, $"{leftOutText} pastas ficaram de fora.", "Elas aparecem na lista como não verificadas."),
        };
        Details = leftOutCount > 0 ? leftOutDetails : null;
        Stage = FolderVerifyStage.Found;
    }

    private static string DescribeLeftOut(LeftOutFolder folder) => folder.Reason switch
    {
        LeftOutReason.Link => folder.Path + " (atalho para outra pasta, não aberto)",
        _ => folder.Path + " (não pôde ser lida)",
    };

    private void ShowUnreadableFolder(string details)
    {
        Heading = "Não foi possível procurar";
        Banner = new Banner(BannerKind.Error, "A pasta não pôde ser lida.", "Confira se ela existe e se o disco está acessível.");
        Details = details;
        Stage = FolderVerifyStage.Found;
    }

    /// <summary>
    /// Verifies the sets found, one after another. Canceling keeps what was verified; the rest is left unverified.
    /// Ends when the last engine ends; any failure is shown on its set, not thrown. Only when <see cref="CanStart"/>.
    /// </summary>
    public async Task StartAsync()
    {
        if (!CanStart)
        {
            throw new InvalidOperationException("Nothing to verify.");
        }

        Stage = FolderVerifyStage.Verifying;
        Heading = "Verificando";
        Banner = null;
        await RunEachAsync(_sets, isRepair: false).ConfigureAwait(true);
    }

    /// <summary>Repairs every set that can be repaired, one after another. Only when <see cref="CanRepairAll"/>.</summary>
    public async Task RepairAllAsync()
    {
        if (!CanRepairAll)
        {
            throw new InvalidOperationException("Nothing to repair.");
        }

        Stage = FolderVerifyStage.Repairing;
        Heading = "Reparando";
        Banner = null;
        await RunEachAsync([.. _sets.Where(set => set.Health == SetHealth.Repairable)], isRepair: true).ConfigureAwait(true);
    }

    /// <summary>Shows the detail of <paramref name="set"/>: its verification as found, or a new one when there is none (after a repair...).</summary>
    public async Task OpenAsync(CheckedSet set)
    {
        if (!CanOpen || _detail is null)
        {
            throw new InvalidOperationException("No detail can be opened now.");
        }

        OpenedSet = set;
        if (set.Verified is { } verified)
        {
            await _detail.ShowAsync(set.RecoveryFile, verified).ConfigureAwait(true);
        }
        else
        {
            await _detail.VerifyAsync(set.RecoveryFile).ConfigureAwait(true);
        }
    }

    /// <summary>Back from a set's detail to the summary.</summary>
    public void CloseDetail() => OpenedSet = null;

    /// <summary>Leaves the folder verification, e.g. when the user opens a single recovery file. Only when nothing runs.</summary>
    public void Leave()
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("A task is running.");
        }

        Reset();
    }

    private void Reset()
    {
        OpenedSet = null;
        ShowSets([], []);
        Folder = null;
        Banner = null;
        Counts = null;
        Details = null;
        Stage = FolderVerifyStage.Idle;
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

    /// <summary>
    /// Stops the search, going back to where the screen was; or stops the set under way and the ones after it,
    /// and the ones already done keep their result.
    /// </summary>
    public void Cancel()
    {
        if (!CanCancel)
        {
            return;
        }

        _stopSearch?.Cancel();
        _cancelRequested = true;
        _run?.Cancel();
    }

    /// <summary>Ends the engine at once if it is still running, e.g. when the window closes.</summary>
    public void Dispose()
    {
        _disposed = true;
        _stopSearch?.Cancel();
        _run?.Dispose();
        if (_detail is not null)
        {
            _detail.Ended -= OnDetailEnded;
            _detail.PropertyChanged -= OnDetailChanged;
        }
    }

    private async Task RunEachAsync(IReadOnlyList<CheckedSet> sets, bool isRepair)
    {
        _cancelRequested = false;
        var root = Folder ?? "";
        for (var index = 0; index < sets.Count; index++)
        {
            var set = sets[index];
            if (_cancelRequested)
            {
                // A repair not started leaves the set as it was: still repairable.
                set.Skip(isRepair ? SetHealth.Repairable : SetHealth.NotVerified);
                continue;
            }

            ProgressText = $"Conjunto {(index + 1).ToString("N0", PtBrazil.Culture)} de {sets.Count.ToString("N0", PtBrazil.Culture)}: {NameIn(root, set.RecoveryFile)}";
            ActivityText = isRepair ? "Conferindo os arquivos antes de reparar" : "Lendo o arquivo de recuperação";
            Percent = null;
            IsPaused = false;
            set.Start(isRepair);
            Counts = CountsOf(Folders);

            EngineCommand command = isRepair ? new EngineCommand.Repair(set.RecoveryFile) : new EngineCommand.Verify(set.RecoveryFile);
            var result = await RunAsync(command).ConfigureAwait(true);
            if (_disposed)
            {
                // The window closed: nothing is shown any more.
                return;
            }

            set.End(new EngineEnded(command, result));
            Counts = CountsOf(Folders);
        }

        ProgressText = null;
        Stage = FolderVerifyStage.Done;
        Heading = (isRepair, _cancelRequested) switch
        {
            (false, false) => "Verificação concluída",
            (false, true) => "Verificação cancelada",
            (true, false) => "Reparo concluído",
            (true, true) => "Reparo cancelado",
        };
        ShowSummary();
    }

    /// <summary>One engine task; a failure to start it or to read its result ends it as an error, with the reason as its output.</summary>
    private async Task<EngineResult> RunAsync(EngineCommand command)
    {
        var runNumber = ++_runNumber;
        try
        {
            // An update posted just before the task ended may arrive after it: ignore it then.
            _run = _startEngine(command, new UiProgress<EngineProgress>(progress =>
            {
                if (runNumber == _runNumber && _run is not null)
                {
                    Percent = progress.Percent;
                    ActivityText = VerifyScreen.ActivityTextOf(progress.Activity);
                }
            }));
            return await _run.Completion.ConfigureAwait(true);
        }
        catch (Exception failure)
        {
            // Shown on the set as unreadable, with the reason in Detalhes técnicos: the other sets still run.
            Details = SetCreation.Join(Details, $"{command.RecoveryFile}:\n{failure}");
            return new EngineResult(EngineOutcome.Error, -1, Report: null, failure.ToString());
        }
        finally
        {
            _run?.Dispose();
            _run = null;
            IsPaused = false;
        }
    }

    private void ShowSummary()
    {
        var counts = CountsOf(Folders);
        Counts = counts;
        var total = counts.Complete + counts.Repairable + counts.Unrepairable + counts.Unverified;
        var notVerified = _sets.Count(set => set.Health == SetHealth.NotVerified);
        var leftOut = Folders.Count(folder => folder.LeftOut is not null);

        // A folder left out counts as not verified, so it never ends here.
        if (counts.Complete == total)
        {
            Banner = new Banner(BannerKind.Success, total == 1 ? "A pasta está íntegra." : $"As {total.ToString("N0", PtBrazil.Culture)} pastas estão íntegras.", Detail: null);
            return;
        }

        var parts = new List<string>(4);
        void Add(int count, string singular, string plural)
        {
            if (count > 0)
            {
                parts.Add(PtBrazil.Count(count, singular, plural));
            }
        }

        Add(counts.Complete, "íntegra", "íntegras");
        Add(counts.Repairable, "com problema reparável", "com problema reparável");
        Add(counts.Unrepairable, "com problema sem reparo", "com problema sem reparo");
        Add(counts.Unverified, "não verificada", "não verificadas");
        var title = PtBrazil.Count(total, "pasta", "pastas") + ": " + JoinAnd(parts) + ".";

        var kind = counts.Unrepairable > 0 ? BannerKind.Error : BannerKind.Warning;
        var notes = new List<string>(2);
        if (notVerified > 0)
        {
            notes.Add(PtBrazil.Count(notVerified, "conjunto ficou", "conjuntos ficaram") + " sem verificar, ou com o reparo interrompido. Verifique a pasta de novo para saber como estão.");
        }

        if (leftOut > 0)
        {
            notes.Add(PtBrazil.Count(leftOut, "pasta ficou", "pastas ficaram") + " de fora da verificação: o motivo está na lista.");
        }

        Banner = new Banner(kind, title, notes.Count > 0 ? string.Join(" ", notes) : null);
    }

    private static string JoinAnd(List<string> parts) =>
        parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " e " + parts[^1];

    private static FolderCounts CountsOf(IReadOnlyList<CheckedFolder> folders) => new(
        folders.Count(folder => folder.Group == HealthGroup.Complete),
        folders.Count(folder => folder.Group == HealthGroup.Repairable),
        folders.Count(folder => folder.Group == HealthGroup.Unrepairable),
        folders.Count(folder => folder.Group == HealthGroup.Unverified));

    private void ShowSets(IReadOnlyList<FoundSet> found, IReadOnlyList<LeftOutFolder> leftOut)
    {
        var root = Folder ?? "";
        _sets = [.. found.Select(set => new CheckedSet(set))];
        Folders =
        [
            .. _sets
                .GroupBy(set => set.Found.Folder, StringComparer.OrdinalIgnoreCase)
                .Select(folder => new CheckedFolder(NameIn(root, folder.Key), folder.Key, [.. folder]))
                .Concat(leftOut.Select(folder => new CheckedFolder(NameIn(root, folder.Path), folder.Path, [], folder.Reason)))
                .OrderBy(folder => folder.Path, TreeOrder.Instance),
        ];
        FoundText = "";
        AnnounceAbilities();
    }

    /// <summary>A path inside the folder chosen, starting with that folder's name: "2019\Natal" for "D:\Fotos\2019\Natal".</summary>
    private static string NameIn(string root, string path) => Path.GetRelativePath(root, path) is var inside && inside == "."
        ? Selection.FolderName(root)
        : Path.Combine(Selection.FolderName(root), inside);

    /// <summary>Paths in the order the search meets them: a folder before what is inside it, siblings by name.</summary>
    private sealed class TreeOrder : IComparer<string>
    {
        public static readonly TreeOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            var (left, right) = ((x ?? "").Split(Path.DirectorySeparatorChar), (y ?? "").Split(Path.DirectorySeparatorChar));
            for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                var order = StringComparer.CurrentCultureIgnoreCase.Compare(left[index], right[index]);
                if (order != 0)
                {
                    return order;
                }
            }

            return left.Length.CompareTo(right.Length);
        }
    }

    private void AnnounceAbilities()
    {
        Announce(nameof(CanStart));
        Announce(nameof(CanOpen));
        Announce(nameof(CanRepairAll));
    }

    private void OnDetailEnded(object? sender, EngineEnded ended)
    {
        if (!IsActive)
        {
            return;
        }

        var set = _sets.FirstOrDefault(set => set.RecoveryFile.Equals(ended.Command.RecoveryFile, StringComparison.OrdinalIgnoreCase));
        if (set is null)
        {
            return;
        }

        set.End(ended);
        if (Stage == FolderVerifyStage.Done)
        {
            ShowSummary();
        }

        AnnounceAbilities();
    }

    private void OnDetailChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VerifyScreen.IsRunning))
        {
            AnnounceAbilities();
        }
    }
}
