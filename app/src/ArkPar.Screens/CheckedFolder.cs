// SPDX-License-Identifier: GPL-2.0-or-later

using System.ComponentModel;

namespace ArkPar.Screens;

/// <summary>
/// A folder of a folder verification: one line of the summary, with the sets that protect it: those directly in
/// it, and the one of its name beside it (<see cref="FoundSet.ProtectedFolder"/>). A folder with one
/// set opens that set; one with several lists those with a problem. A folder the search could not look into is a
/// line too, never verified, so the summary never says more was verified than was.
/// </summary>
public sealed class CheckedFolder : Observable
{
    internal CheckedFolder(string name, string path, IReadOnlyList<CheckedSet> sets, LeftOutReason? leftOut = null)
    {
        Name = name;
        Path = path;
        Sets = sets;
        LeftOut = leftOut;
        foreach (var set in sets)
        {
            set.PropertyChanged += OnSetChanged;
        }
    }

    /// <summary>The folder inside the one chosen, starting with its name, e.g. <c>2019\Natal\Ceia</c>.</summary>
    public string Name { get; }

    /// <summary>The folder in full.</summary>
    public string Path { get; }

    /// <summary>The sets summed up under the folder, in the order they are verified.</summary>
    public IReadOnlyList<CheckedSet> Sets { get; }

    /// <summary>Why the search did not look into the folder; null when it did. Its sets, if any, are unknown.</summary>
    public LeftOutReason? LeftOut { get; }

    public bool HasSeveralSets => Sets.Count > 1;

    /// <summary>Which sets the folder holds: "Praia.par2", or "300 conjuntos"; nothing when it was left out.</summary>
    public string SetsText => LeftOut is not null ? "" : Sets is [var only] ? only.FileName : PtBrazil.Count(Sets.Count, "conjunto", "conjuntos");

    // A folder is as bad as its worst set (GLOSSARY.md, folder verification): the groups with a problem, worst first.
    private static readonly HealthGroup[] WorstFirst = [HealthGroup.Unrepairable, HealthGroup.Repairable, HealthGroup.Unverified];

    /// <summary>The folder's group: not verified when left out; else its worst set's, once every set has one; null before.</summary>
    public HealthGroup? Group =>
        LeftOut is not null ? HealthGroup.Unverified
        : IsPending ? null
        : WorstFirst.Cast<HealthGroup?>().FirstOrDefault(group => Sets.Any(set => set.Group == group)) ?? HealthGroup.Complete;

    /// <summary>
    /// How the folder is: its set's text, or for several sets how many are in each group, e.g.
    /// "298 íntegros · 2 com problema reparável".
    /// </summary>
    public string HealthText
    {
        get
        {
            switch (LeftOut)
            {
                case LeftOutReason.Unreadable:
                    return "Não pôde ser lida · nada nela foi verificado";
                case LeftOutReason.Link:
                    return "Atalho para outra pasta, não aberto · nada nele foi verificado";
                case null:
                    break;
            }

            if (Sets is [var only])
            {
                return only.HealthText;
            }

            int Count(Func<CheckedSet, bool> which) => Sets.Count(which);
            var parts = new List<string>(5);
            void Add(int count, string singular, string plural)
            {
                if (count > 0)
                {
                    parts.Add(PtBrazil.Count(count, singular, plural));
                }
            }

            Add(Count(set => set.Group == HealthGroup.Complete), "íntegro", "íntegros");
            Add(Count(set => set.Group == HealthGroup.Repairable), "com problema reparável", "com problema reparável");
            Add(Count(set => set.Group == HealthGroup.Unrepairable), "com problema sem reparo", "com problema sem reparo");
            Add(Count(set => set.Group == HealthGroup.Unverified), "não verificado", "não verificados");
            Add(Count(set => set.Group is null), "aguardando", "aguardando");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// What a folder with several sets opens into: every set while some are still to verify, so all the sets found
    /// are listed before starting; then only those with a problem or not verified.
    /// </summary>
    public IReadOnlyList<CheckedSet> ListedSets => IsPending ? Sets : [.. Sets.Where(set => set.Group is not HealthGroup.Complete)];

    /// <summary>The folder has several sets, and some to list: it opens into <see cref="ListedSets"/>.</summary>
    public bool HasSetList => HasSeveralSets && ListedSets.Count > 0;

    /// <summary>The button that opens <see cref="ListedSets"/>.</summary>
    public string SetListText => IsPending ? "Ver os conjuntos" : "Ver os conjuntos com problema";

    private bool IsPending => Sets.Any(set => set.Group is null);

    private void OnSetChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CheckedSet.Health) or nameof(CheckedSet.HealthText))
        {
            Announce(nameof(Group));
            Announce(nameof(HealthText));
            Announce(nameof(ListedSets));
            Announce(nameof(HasSetList));
            Announce(nameof(SetListText));
        }
    }
}
