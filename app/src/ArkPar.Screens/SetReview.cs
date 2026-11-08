// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;

namespace ArkPar.Screens;

/// <summary>
/// The list the Create screen shows before creating: each planned set, compared with the set of its name already
/// in its folder, if any, and included or left out (<see cref="ReviewedSet.IsIncluded"/>). Below them, the
/// orphaned sets in the folders chosen (<see cref="OrphanedSets"/>), only as warnings. Use it from the screen's
/// thread; it announces <see cref="Included"/> whenever what the create would make changes.
/// </summary>
public sealed class SetReview(Func<string, CancellationToken, Task<SetListing>> listSet) : Observable
{
    private IReadOnlyList<ReviewedSet> _items = [];
    private IReadOnlyList<OrphanedSet> _orphanedSets = [];
    private bool _isComparing;
    private bool _isFindingOrphanedSets;
    private int _comparedCount;
    private readonly Lock _orphanedSetsLock = new();
    private CancellationTokenSource? _stop;

    /// <summary>One line for each planned set, in the plan's order.</summary>
    public IReadOnlyList<ReviewedSet> Items
    {
        get => _items;
        private set
        {
            foreach (var item in _items)
            {
                item.PropertyChanged -= OnItemChanged;
            }

            _items = value;
            foreach (var item in _items)
            {
                item.PropertyChanged += OnItemChanged;
            }

            Announce(nameof(Items));
            Announce(nameof(Included));
        }
    }

    /// <summary>The orphaned sets found so far (GLOSSARY.md), by folder, then by name. The create never makes them.</summary>
    public IReadOnlyList<OrphanedSet> OrphanedSets
    {
        get => _orphanedSets;
        private set
        {
            if (Set(ref _orphanedSets, value))
            {
                Announce(nameof(Details));
            }
        }
    }

    /// <summary>The sets the create would make, in the plan's order.</summary>
    public IReadOnlyList<PlannedSet> Included => [.. Items.Where(item => item.IsIncluded).Select(item => item.Planned)];

    /// <summary>The engine is still listing sets already in the folders.</summary>
    public bool IsComparing
    {
        get => _isComparing;
        private set
        {
            if (Set(ref _isComparing, value))
            {
                Announce(nameof(Details));
            }
        }
    }

    /// <summary>
    /// The engine is listing the other sets in the folders chosen, to find the orphaned ones. True only once
    /// <see cref="IsComparing"/> ends, and creating does not wait for it: a create never touches a set of another name.
    /// </summary>
    public bool IsFindingOrphanedSets { get => _isFindingOrphanedSets; private set => Set(ref _isFindingOrphanedSets, value); }

    /// <summary>How far the comparing is, e.g. "Conferindo os conjuntos que já existem… 3 de 12".</summary>
    public string ComparingText => $"Conferindo os conjuntos que já existem… {_comparedCount.ToString("N0", PtBrazil.Culture)} de {Items.Count.ToString("N0", PtBrazil.Culture)}";

    /// <summary>Why sets already in the folders could not be read, in technical terms; null when every one could.</summary>
    public string? Details => Items
        .Select(item => (File: item.Planned.RecoveryFile, item.Comparison.Details))
        .Concat(OrphanedSets.Select(set => (File: set.Path, set.Details)))
        .Where(set => set.Details is not null)
        .Select(set => $"{set.File}:\n{set.Details}")
        .Aggregate((string?)null, (all, details) => SetCreation.Join(all, details));

    private int ComparedCount
    {
        get => _comparedCount;
        set
        {
            if (Set(ref _comparedCount, value))
            {
                Announce(nameof(ComparingText));
            }
        }
    }

    /// <summary>
    /// Compares <paramref name="sets"/> with what is in their folders, replacing the list, then looks for the
    /// orphaned sets in <see cref="Selection.SetFolders"/> of <paramref name="selection"/>. Each folder is read
    /// here, once: a set with no files there is missing at once. The sets there are listed by the engine, a few at
    /// once, off this call; a later <see cref="Start"/> or <see cref="Stop"/> stops them.
    /// </summary>
    public void Start(IReadOnlyList<PlannedSet> sets, Selection selection)
    {
        Stop();
        var items = sets.Select(set => new ReviewedSet(set)).ToList();
        var pending = new List<(ReviewedSet Item, IReadOnlyList<string> SetFiles)>();

        // Each folder's sets by name; null for a folder that cannot be listed now.
        var setsIn = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>?>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in items.GroupBy(item => item.Planned.BaseDirectory, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var setsThere = Selection.SetFilesIn(folder.Key);
                setsIn[folder.Key] = setsThere;
                foreach (var item in folder)
                {
                    if (setsThere.TryGetValue(item.Planned.Name, out var setFiles))
                    {
                        pending.Add((item, setFiles));
                    }
                    else
                    {
                        item.Comparison = SetComparison.Missing;
                    }
                }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be listed now: its sets wait for the user, with the reason in Detalhes técnicos.
                setsIn[folder.Key] = null;
                foreach (var item in folder)
                {
                    item.Comparison = new SetComparison(SetState.Unreadable, Details: failure.ToString());
                }
            }
        }

        var (candidates, unreadable) = OrphanCandidatesOf(items, selection, setsIn);
        Items = items;
        OrphanedSets = unreadable;
        ComparedCount = items.Count - pending.Count;
        Announce(nameof(ComparingText));
        IsComparing = pending.Count > 0;
        IsFindingOrphanedSets = pending.Count == 0 && candidates.Count > 0;
        if (pending.Count == 0 && candidates.Count == 0)
        {
            return;
        }

        var stop = new CancellationTokenSource();
        _stop = stop;

        // The loop shows every failure on its item.
        _ = ReviewAsync(pending, candidates, stop);
    }

    /// <summary>Takes <paramref name="sets"/> off <see cref="OrphanedSets"/>, once deleted.</summary>
    public void Remove(IReadOnlyCollection<OrphanedSet> sets)
    {
        lock (_orphanedSetsLock)
        {
            OrphanedSets = [.. OrphanedSets.Where(set => !sets.Contains(set))];
        }
    }

    /// <summary>Stops the comparing under way: its answers are no longer wanted.</summary>
    public void Stop()
    {
        _stop?.Cancel();
        _stop = null;
        IsComparing = false;
        IsFindingOrphanedSets = false;
    }

    /// <summary>
    /// The sets in the folders chosen that no planned set has the name of, to list to know whether they are orphaned
    /// (each as a set covering nothing, so every file it lists is moved out or removed); and a warning for each of
    /// those folders that cannot be read.
    /// </summary>
    private static (List<(PlannedSet Set, IReadOnlyList<string> SetFiles)> Candidates, List<OrphanedSet> Unreadable) OrphanCandidatesOf(
        List<ReviewedSet> items,
        Selection selection,
        Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>?> setsIn)
    {
        var candidates = new List<(PlannedSet Set, IReadOnlyList<string> SetFiles)>();
        var unreadable = new List<OrphanedSet>();
        if (selection.BaseDirectory is not { } chosenFolder)
        {
            return (candidates, unreadable);
        }

        var planned = items.Select(item => item.Planned.RecoveryFile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in selection.SetFolders)
        {
            var where = CreationPlan.WhereOf(folder, chosenFolder);
            if (!setsIn.TryGetValue(folder, out var setsThere))
            {
                try
                {
                    setsThere = Selection.SetFilesIn(folder);
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                {
                    unreadable.Add(new OrphanedSet(Selection.FolderName(folder), folder, where, "Não foi possível ler a pasta; o motivo está em Detalhes técnicos", failure.ToString())
                    {
                        IsFolder = true,
                    });
                    continue;
                }
            }

            // A folder of planned sets that cannot be listed: they say so already.
            if (setsThere is null)
            {
                continue;
            }

            foreach (var (name, setFiles) in setsThere)
            {
                var recoveryFile = Path.Combine(folder, name + Selection.RecoveryExtension);

                // A set being written ("Viagem.arkpar-novo") or not yet renamed ("Viagem.arkpar-pronto") is not one yet.
                if (!planned.Contains(recoveryFile) && !name.EndsWith(SetCreation.DraftSuffix, StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(SetCreation.ReadySuffix, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add((new PlannedSet(recoveryFile, folder, [], 0) { Where = where }, setFiles));
                }
            }
        }

        return (candidates, unreadable);
    }

    /// <summary>Compares the <paramref name="pending"/> planned sets, then lists the <paramref name="candidates"/> to find the orphaned sets.</summary>
    private async Task ReviewAsync(
        List<(ReviewedSet Item, IReadOnlyList<string> SetFiles)> pending,
        List<(PlannedSet Set, IReadOnlyList<string> SetFiles)> candidates,
        CancellationTokenSource stop)
    {
        try
        {
            await EachAsync(pending, stop, async set =>
            {
                if (await CompareAsync(set.Item.Planned, set.SetFiles, stop).ConfigureAwait(true) is { } comparison)
                {
                    set.Item.Comparison = comparison;
                    ComparedCount++;
                }
            }).ConfigureAwait(true);
            if (stop.IsCancellationRequested)
            {
                return;
            }

            // In this order, so the screen is never told that both are over while candidates remain.
            IsFindingOrphanedSets = candidates.Count > 0;
            IsComparing = false;
            await EachAsync(candidates, stop, async candidate =>
            {
                if (await CompareAsync(candidate.Set, candidate.SetFiles, stop).ConfigureAwait(true) is { } comparison
                    && OrphanedSetOf(candidate.Set, comparison) is { } orphaned)
                {
                    // Without a screen's thread (tests, a caller off it), two answers may land at once: neither row is lost.
                    lock (_orphanedSetsLock)
                    {
                        OrphanedSets =
                        [
                            .. OrphanedSets.Append(orphaned)
                                .OrderBy(set => Path.GetDirectoryName(set.Path), StringComparer.CurrentCultureIgnoreCase)
                                .ThenBy(set => set.Name, StringComparer.CurrentCultureIgnoreCase),
                        ];
                    }
                }
            }).ConfigureAwait(true);
            if (!stop.IsCancellationRequested)
            {
                _stop = null;
                IsFindingOrphanedSets = false;
            }
        }
        finally
        {
            stop.Dispose();
        }
    }

    /// <summary>The comparison of the set there, whose files are <paramref name="setFiles"/>; null once stopped.</summary>
    private async Task<SetComparison?> CompareAsync(PlannedSet set, IReadOnlyList<string> setFiles, CancellationTokenSource stop)
    {
        try
        {
            var comparison = await SetComparison.CompareAsync(set, setFiles, listSet, stop.Token).ConfigureAwait(true);

            // Stopped meanwhile (the mode changed...): the answer belongs to a list no longer shown.
            return stop.IsCancellationRequested ? null : comparison;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs <paramref name="step"/> on each of <paramref name="work"/>, several waiting at once, until done or stopped.
    /// Every step between the waits runs on the screen's thread (ConfigureAwait(true)), one at a time: the shared
    /// counters need no locking.
    /// </summary>
    private static Task EachAsync<T>(IReadOnlyList<T> work, CancellationTokenSource stop, Func<T, Task> step)
    {
        var next = 0;
        async Task NextAsync()
        {
            for (var index = next++; index < work.Count && !stop.IsCancellationRequested; index = next++)
            {
                await step(work[index]).ConfigureAwait(true);
            }
        }

        return Task.WhenAll(Enumerable.Range(0, Math.Min(work.Count, Environment.ProcessorCount)).Select(_ => NextAsync()));
    }

    /// <summary>The warning for a set no planned set has the name of: only when it lost files, or cannot be read.</summary>
    private static OrphanedSet? OrphanedSetOf(PlannedSet set, SetComparison comparison) => comparison switch
    {
        { State: SetState.Unreadable } => new(
            Path.GetFileName(set.RecoveryFile), set.RecoveryFile, set.Where, "Não foi possível ler o conjunto; o motivo está em Detalhes técnicos", comparison.Details),
        { RemovedFiles: > 0 } => new(
            Path.GetFileName(set.RecoveryFile),
            set.RecoveryFile,
            set.Where,
            PtBrazil.Count(comparison.RemovedFiles, "arquivo removido", "arquivos removidos")
                + comparison.MovedOutFiles switch
                {
                    0 => "",
                    1 => "; ainda protege 1 que está no disco",
                    var stillThere => $"; ainda protege {stillThere.ToString("N0", PtBrazil.Culture)} que estão no disco",
                }
                + ", use Verificar e reparar")
        {
            // Covering nothing planned, every file it lists that is still on the disk counts as moved out.
            StillProtected = comparison.MovedOutFiles,
        },
        _ => null,
    };

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // A comparison that leaves the set included still changes what the create would do: replace it, or not.
        if (e.PropertyName is nameof(ReviewedSet.IsIncluded) or nameof(ReviewedSet.Comparison))
        {
            Announce(nameof(Included));
        }

        if (e.PropertyName == nameof(ReviewedSet.Comparison))
        {
            Announce(nameof(Details));
        }
    }
}
