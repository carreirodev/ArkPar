// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;

namespace ArkPar.Screens;

/// <summary>
/// What every screen that runs engine tasks shows of the one under way, and of how it ended: the activity, how
/// far it is, whether it is paused, and the result's headline.
/// </summary>
public abstract class EngineTaskScreen : Observable
{
    private string _activityText = "";
    private double? _percent;
    private bool _isPaused;
    private Banner? _banner;

    /// <summary>What the engine is doing right now, in the user's words, e.g. "Lendo praia 01.jpg".</summary>
    public string ActivityText { get => _activityText; protected set => Set(ref _activityText, value); }

    /// <summary>How far the current activity is, from 0 to 100; null before the engine reports any.</summary>
    public double? Percent
    {
        get => _percent;
        protected set
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
        protected set
        {
            if (Set(ref _isPaused, value))
            {
                Announce(nameof(PauseText));
            }
        }
    }

    public string PauseText => IsPaused ? "Continuar" : "Pausar";

    /// <summary>The headline of what the screen has to tell: a result, or what was found; null when there is none.</summary>
    public Banner? Banner
    {
        get => _banner;
        protected set
        {
            if (Set(ref _banner, value))
            {
                Announce(nameof(HasBanner));
            }
        }
    }

    public bool HasBanner => Banner is not null;

    /// <summary>Pauses <paramref name="run"/>, or lets it go on; nothing without a task under way.</summary>
    protected void TogglePause(IEngineRun? run)
    {
        if (run is null)
        {
            return;
        }

        run.SetPaused(!run.IsPaused);
        IsPaused = run.IsPaused;
    }
}
