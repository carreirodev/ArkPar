// SPDX-License-Identifier: GPL-2.0-or-later

namespace ArkPar.Screens;

/// <summary>
/// One line of the list the Create screen shows before creating: a planned set, what is in its folder now, and
/// whether the create makes it (<see cref="IsIncluded"/>, which the user can change).
/// </summary>
public sealed class ReviewedSet : Observable
{
    private SetComparison _comparison = SetComparison.Comparing;
    private bool _isIncluded = true;

    internal ReviewedSet(PlannedSet set)
    {
        Planned = set;
    }

    public PlannedSet Planned { get; }

    public SetComparison Comparison
    {
        get => _comparison;
        internal set
        {
            if (!Set(ref _comparison, value))
            {
                return;
            }

            // A set that may be the only way back to a lost file, or that could not be read, waits for the user;
            // one up to date is skipped.
            IsIncluded = value.State switch
            {
                SetState.Missing => true,
                SetState.Stale => !value.MayRecoverLosses,
                _ => false,
            };
            Announce(nameof(IsCompared));
            Announce(nameof(StatusText));
            Announce(nameof(Caution));
        }
    }

    /// <summary>Whether the set is compared, so it can be included or left out.</summary>
    public bool IsCompared => Comparison.State != SetState.Comparing;

    /// <summary>Whether the create makes this set: by default, those missing and those stale.</summary>
    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (Set(ref _isIncluded, value))
            {
                Announce(nameof(StatusText));
            }
        }
    }

    /// <summary>
    /// What the set needs and why, e.g. "Refazer · 30 arquivos novos" or "Pular · em dia". The box says whether the
    /// create does it: a set to remake may start unchecked ("Refazer · 1 arquivo removido"). Only a set up to date
    /// follows the box, as remaking it is the user's own choice.
    /// </summary>
    public string StatusText
    {
        get
        {
            if (!IsCompared)
            {
                return "Conferindo o conjunto atual…";
            }

            var action = Comparison.State switch
            {
                SetState.Missing => "Criar",
                SetState.UpToDate => IsIncluded ? "Refazer" : "Pular",
                _ => "Refazer",
            };
            return action + " · " + Reason;
        }
    }

    /// <summary>What to do before remaking the set, when remaking it may lose something; otherwise null.</summary>
    public string? Caution => Comparison switch
    {
        { State: SetState.Stale, MayRecoverLosses: true } =>
            "Se não foi você quem apagou ou mudou esses arquivos, use Verificar e reparar antes: o conjunto atual pode recuperá-los.",
        { State: SetState.Unreadable } => "O motivo está em Detalhes técnicos.",
        _ => null,
    };

    private string Reason => Comparison switch
    {
        { State: SetState.Missing } => "sem conjunto",
        { State: SetState.UpToDate } => "em dia",
        { State: SetState.Unreadable } => "não foi possível ler o conjunto atual",
        var stale => string.Join(", ", new[]
        {
            stale.NewFiles > 0 ? PtBrazil.Count(stale.NewFiles, "arquivo novo", "arquivos novos") : null,
            stale.ChangedFiles > 0 ? PtBrazil.Count(stale.ChangedFiles, "arquivo alterado", "arquivos alterados") : null,
            stale.RemovedFiles > 0 ? PtBrazil.Count(stale.RemovedFiles, "arquivo removido", "arquivos removidos") : null,

            // The files left out are still on the disk, maybe in sets of their own: say how much each set covers.
            stale.MovedOutFiles > 0
                ? $"o atual protege {PtBrazil.Count(stale.ListedFiles, "arquivo", "arquivos")} e o novo protegerá {Planned.Files.Count.ToString("N0", PtBrazil.Culture)}"
                : null,
        }.OfType<string>()),
    };
}
