// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using ArkPar.Engine;

namespace ArkPar.Screens;

/// <summary>
/// The Create screen's estimates: asks the engine for the trial of each set the screen wants one of, one round at
/// a time, and keeps the answers, so a box ticked again, or a setting put back, needs no new trial. A settings
/// change while a round runs stops it and asks for one more (<see cref="Restart"/>); a change in what is included
/// only asks for one more (<see cref="Refresh"/>). Use it from the screen's thread.
/// </summary>
/// <param name="runTrial">Runs the engine's trial of a create.</param>
/// <param name="mayEstimate">Whether the screen has something to estimate now.</param>
/// <param name="wantedTrials">The trials the screen wants answered now: the sets included, with the current settings.</param>
/// <param name="answered">
/// A round ended and its answer still holds: with why it failed, or with null once every trial it asked has its
/// estimate (<see cref="TryGet"/>).
/// </param>
internal sealed class EstimateQueue(
    Func<EngineCommand.Create, CancellationToken, Task<RecoveryEstimate>> runTrial,
    Func<bool> mayEstimate,
    Func<IEnumerable<EngineCommand.Create>> wantedTrials,
    Action<Exception?> answered) : Observable
{
    private bool _again;
    private int _number;
    private CancellationTokenSource? _stop;

    // The engine's estimate of each trial asked (a set and the settings). The answers come on the screen's thread
    // (see RunTrialsAsync); without one (the engine's own tests) two may land at once, where a count off by one
    // shows nothing wrong but a broken dictionary would, hence concurrent.
    private ConcurrentDictionary<EngineCommand.Create, RecoveryEstimate> _estimates = new();
    private bool _isEstimating;
    private int _estimatedSets;
    private int _estimatingSets;

    // The trials of the round under way.
    private HashSet<EngineCommand.Create> _roundTrials = [];

    // The trial whose failure was told: it stands, not tried again, until a setting changes or its set is left out.
    private EngineCommand.Create? _failedTrial;

    /// <summary>The engine is working out the sizes for the latest settings.</summary>
    public bool IsEstimating { get => _isEstimating; private set => Set(ref _isEstimating, value); }

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

    /// <summary>The estimate kept for <paramref name="trial"/>, if the engine has answered it.</summary>
    public bool TryGet(EngineCommand.Create trial, [MaybeNullWhen(false)] out RecoveryEstimate estimate) =>
        _estimates.TryGetValue(trial, out estimate);

    /// <summary>Drops every estimate kept: a new plan, or files that may have changed. A round under way keeps its own.</summary>
    public void Forget() => _estimates = new();

    /// <summary>A setting changed, or a create stopped: the answers under way are not wanted, and a new round follows.</summary>
    public void Restart()
    {
        if (!mayEstimate())
        {
            // An answer under way is not wanted: what it was for has changed.
            Stop();
            return;
        }

        _number++;
        _failedTrial = null;

        // IsEstimating is set before the loop's first wait, so a round that ends at once is never mistaken for one under way.
        if (IsEstimating)
        {
            // Again and stopped, in this order: a trial that stops at once goes straight on to the next round.
            _again = true;
            _stop?.Cancel();
            return;
        }

        // The loop tells every failure itself.
        _ = RunAsync();
    }

    /// <summary>
    /// What is included changed: the round under way goes on, its answers still hold, and one more follows for the
    /// sets it does not cover. A set left out is not asked about, so its failure no longer stops the others.
    /// </summary>
    public void Refresh()
    {
        if (!mayEstimate())
        {
            Stop();
            return;
        }

        if (IsEstimating)
        {
            var wanted = wantedTrials().ToHashSet();
            if (_roundTrials.Any(trial => !wanted.Contains(trial) && !_estimates.ContainsKey(trial)))
            {
                // The round waits on a set just left out: its answer, or its failure, is not wanted. The answers
                // already in stay. Again and stopped, in this order, as in Restart.
                _again = true;
                _stop?.Cancel();
            }
            else if (wanted.Any(trial => !_roundTrials.Contains(trial) && !_estimates.ContainsKey(trial)))
            {
                _again = true;
            }

            return;
        }

        // The loop tells every failure itself.
        _ = RunAsync();
    }

    /// <summary>Stops the round under way, with no other after it: its answer is not wanted.</summary>
    public void Stop()
    {
        _number++;
        _failedTrial = null;
        _again = false;
        _stop?.Cancel();
    }

    private async Task RunAsync()
    {
        IsEstimating = true;
        try
        {
            do
            {
                _again = false;
                if (_failedTrial is { } failed && wantedTrials().Contains(failed))
                {
                    // The failure told still stands: asking again would only repeat it.
                    break;
                }

                var number = _number;
                var estimates = _estimates;
                var trials = wantedTrials().Where(trial => !estimates.ContainsKey(trial)).ToList();
                _roundTrials = [.. trials];
                using var stop = new CancellationTokenSource();
                _stop = stop;
                try
                {
                    var answer = await RunTrialsAsync(trials, estimates, stop.Token).ConfigureAwait(true);
                    if (number != _number)
                    {
                        continue;
                    }

                    _failedTrial = answer?.Trial;
                    answered(answer?.Failure);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    // Stopped because a setting or what was chosen changed: its answer is not wanted, but a newer one may be.
                    continue;
                }
                catch (Exception failure)
                {
                    // Not a trial's: told all the same, with its details.
                    if (number == _number)
                    {
                        answered(failure);
                    }
                }
                finally
                {
                    _stop = null;
                }
            }
            while (_again);
        }
        finally
        {
            _roundTrials = [];
            IsEstimating = false;
        }
    }

    /// <summary>
    /// Runs the engine's <paramref name="trials"/>, keeping each answer in <paramref name="estimates"/> as it comes.
    /// A few run at once: each is a short engine run that reads only the files' sizes. The first that fails stops
    /// the others and is returned; the answers already in stay. Null when every trial answered.
    /// </summary>
    private async Task<(EngineCommand.Create Trial, Exception Failure)?> RunTrialsAsync(
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
        async Task NextAsync()
        {
            for (var index = next++; index < trials.Count && !round.IsCancellationRequested; index = next++)
            {
                try
                {
                    estimates[trials[index]] = await runTrial(trials[index], round.Token).ConfigureAwait(true);
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
            await Task.WhenAll(Enumerable.Range(0, Math.Min(trials.Count, Environment.ProcessorCount)).Select(_ => NextAsync())).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (round.IsCancellationRequested)
        {
            // The others, stopped: by a newer change (thrown below) or by the first failure (returned below).
        }

        stop.ThrowIfCancellationRequested();
        return firstFailure;
    }
}
