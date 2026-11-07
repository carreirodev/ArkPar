// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;

namespace ArkPar.Screens;

/// <summary>
/// The list the Create screen shows before creating: each planned set, compared with the set of its name already
/// in its folder, if any, and included or left out (<see cref="ReviewedSet.IsIncluded"/>). Use it from the
/// screen's thread; it announces <see cref="Included"/> whenever what the create would make changes.
/// </summary>
public sealed class SetReview(Func<string, CancellationToken, Task<SetListing>> listSet) : Observable
{
    private IReadOnlyList<ReviewedSet> _items = [];
    private bool _isComparing;
    private int _comparedCount;
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

    /// <summary>How far the comparing is, e.g. "Conferindo os conjuntos que já existem… 3 de 12".</summary>
    public string ComparingText => $"Conferindo os conjuntos que já existem… {_comparedCount.ToString("N0", PtBrazil.Culture)} de {Items.Count.ToString("N0", PtBrazil.Culture)}";

    /// <summary>Why sets already in the folders could not be read, in technical terms; null when every one could.</summary>
    public string? Details => Items
        .Where(item => item.Comparison.Details is not null)
        .Select(item => $"{item.Planned.RecoveryFile}:\n{item.Comparison.Details}")
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
    /// Compares <paramref name="sets"/> with what is in their folders, replacing the list. Each folder is read
    /// here, once: a set with no files there is missing at once. Those that have some are listed by the engine,
    /// a few at once, off this call; a later <see cref="Start"/> or <see cref="Stop"/> stops them.
    /// </summary>
    public void Start(IReadOnlyList<PlannedSet> sets)
    {
        Stop();
        var items = sets.Select(set => new ReviewedSet(set)).ToList();
        var pending = new List<(ReviewedSet Item, IReadOnlyList<string> SetFiles)>();
        foreach (var folder in items.GroupBy(item => item.Planned.BaseDirectory, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var setsThere = Selection.SetFilesIn(folder.Key);
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
                foreach (var item in folder)
                {
                    item.Comparison = new SetComparison(SetState.Unreadable, Details: failure.ToString());
                }
            }
        }

        Items = items;
        ComparedCount = items.Count - pending.Count;
        Announce(nameof(ComparingText));
        if (pending.Count == 0)
        {
            IsComparing = false;
            return;
        }

        IsComparing = true;
        var stop = new CancellationTokenSource();
        _stop = stop;

        // The loop shows every failure on its item.
        _ = CompareAsync(pending, stop);
    }

    /// <summary>Stops the comparing under way: its answers are no longer wanted.</summary>
    public void Stop()
    {
        _stop?.Cancel();
        _stop = null;
        IsComparing = false;
    }

    private async Task CompareAsync(List<(ReviewedSet Item, IReadOnlyList<string> SetFiles)> pending, CancellationTokenSource stop)
    {
        var next = 0;

        // Several listings wait at once, but every step between the waits runs on the screen's thread
        // (ConfigureAwait(true)), one at a time: the shared counters need no locking.
        async Task CompareNextAsync()
        {
            for (var index = next++; index < pending.Count && !stop.IsCancellationRequested; index = next++)
            {
                var (item, setFiles) = pending[index];
                SetComparison comparison;
                try
                {
                    comparison = await SetComparison.CompareAsync(item.Planned, setFiles, listSet, stop.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }

                // Stopped meanwhile (the mode changed...): this row and its count belong to a list no longer shown.
                if (stop.IsCancellationRequested)
                {
                    return;
                }

                item.Comparison = comparison;
                ComparedCount++;
            }
        }

        try
        {
            await Task.WhenAll(Enumerable.Range(0, Math.Min(pending.Count, Environment.ProcessorCount)).Select(_ => CompareNextAsync())).ConfigureAwait(true);
            if (!stop.IsCancellationRequested)
            {
                _stop = null;
                IsComparing = false;
            }
        }
        finally
        {
            stop.Dispose();
        }
    }

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
