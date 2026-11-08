// SPDX-License-Identifier: GPL-2.0-or-later

namespace ArkPar.Screens;

/// <summary>How a selection is split into recovery sets; the user picks one each time (GLOSSARY.md).</summary>
public enum CreationMode
{
    /// <summary>One set covering everything chosen, subfolders included.</summary>
    SingleSet,

    /// <summary>One set in each direct subfolder, covering everything inside it; files loose beside the subfolders get one of their own.</summary>
    PerSubfolder,

    /// <summary>One set in every folder that directly holds files, at any depth, covering only its own files.</summary>
    PerFolder,

    /// <summary>One set beside each file.</summary>
    PerFile,
}

/// <summary>One recovery set a create will make.</summary>
/// <param name="RecoveryFile">Its index file, directly in <paramref name="BaseDirectory"/>.</param>
/// <param name="BaseDirectory">The folder it lives in; every one of <paramref name="Files"/> is inside it.</param>
/// <param name="Files">The files it protects, in full.</param>
/// <param name="TotalSize">How much they hold, in bytes.</param>
public sealed record PlannedSet(string RecoveryFile, string BaseDirectory, IReadOnlyList<string> Files, long TotalSize)
{
    /// <summary>The set's name, as its files start: "07 - Viagem Bahia" for "07 - Viagem Bahia.par2".</summary>
    public string Name => Path.GetFileNameWithoutExtension(RecoveryFile);

    /// <summary>What it protects, e.g. "3 arquivos · 12 KB".</summary>
    public string Meta => PtBrazil.Count(Files.Count, "arquivo", "arquivos") + " · " + SizeText.Of(TotalSize);

    /// <summary>Its folder, from the folder holding what was chosen: "2019\07 - Viagem Bahia".</summary>
    public string Where { get; init; } = "";
}

/// <summary>The recovery sets a create will make of a selection, in one <see cref="CreationMode"/>.</summary>
public sealed class CreationPlan
{
    /// <summary>The most files one recovery set can protect (the PAR2 format's limit, enforced by the engine).</summary>
    public const int MaxFiles = 32768;

    private CreationPlan(IReadOnlyList<PlannedSet> sets, string? problem)
    {
        Sets = sets;
        Problem = problem;
    }

    public static CreationPlan Empty { get; } = new([], null);

    /// <summary>The sets, by folder, then by name; the order they are made in.</summary>
    public IReadOnlyList<PlannedSet> Sets { get; }

    /// <summary>Why the sets cannot be made, in the user's words; null when they can.</summary>
    public string? Problem { get; }

    /// <summary>Splits <paramref name="selection"/> into sets; each one's files keep the selection's order.</summary>
    /// <param name="maxFiles">The most files one set may protect (<see cref="MaxFiles"/>; lower in tests).</param>
    public static CreationPlan For(Selection selection, CreationMode mode, int maxFiles = MaxFiles)
    {
        if (selection.Problem is not null || selection.BaseDirectory is not { } baseDirectory || selection.RecoveryFile is not { } recoveryFile)
        {
            return Empty;
        }

        var files = selection.Files.Select((file, index) => (Path: file, Size: selection.FileSizes[index])).ToList();
        IReadOnlyList<PlannedSet> sets = mode switch
        {
            CreationMode.SingleSet => [SetOf(recoveryFile, baseDirectory, files, baseDirectory)],
            CreationMode.PerSubfolder => ByFolder(files, file => SubfolderOf(file, baseDirectory), baseDirectory),
            CreationMode.PerFolder => ByFolder(files, file => Path.GetDirectoryName(file) ?? baseDirectory, baseDirectory),
            CreationMode.PerFile => Ordered(files.Select(file =>
            {
                // Named with the extension: "IMG_4469.JPG" and "IMG_4469.MOV" (a photo and its live video) keep apart.
                var folder = Path.GetDirectoryName(file.Path) ?? baseDirectory;
                return SetOf(Path.Combine(folder, Path.GetFileName(file.Path) + Selection.RecoveryExtension), folder, [file], baseDirectory);
            })),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown creation mode."),
        };

        return new CreationPlan(sets, ProblemOf(sets, maxFiles));
    }

    /// <summary>One set in each folder <paramref name="folderOf"/> gives, named after it.</summary>
    private static List<PlannedSet> ByFolder(List<(string Path, long Size)> files, Func<string, string> folderOf, string chosenFolder) =>
        Ordered(files
            .GroupBy(file => folderOf(file.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => SetOf(Path.Combine(group.Key, Selection.FolderName(group.Key) + Selection.RecoveryExtension), group.Key, [.. group], chosenFolder)));

    /// <summary>The direct subfolder of <paramref name="baseDirectory"/> holding <paramref name="file"/>, or the folder itself for a file loose in it.</summary>
    private static string SubfolderOf(string file, string baseDirectory)
    {
        var relative = Path.GetRelativePath(baseDirectory, file);
        var separator = relative.IndexOf(Path.DirectorySeparatorChar);
        return separator < 0 ? baseDirectory : Path.Combine(baseDirectory, relative[..separator]);
    }

    private static List<PlannedSet> Ordered(IEnumerable<PlannedSet> sets) =>
    [
        .. sets
            .OrderBy(set => set.BaseDirectory, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(set => set.Name, StringComparer.CurrentCultureIgnoreCase),
    ];

    /// <param name="chosenFolder">The folder holding what was chosen (<see cref="Selection.BaseDirectory"/>), where <see cref="PlannedSet.Where"/> starts.</param>
    private static PlannedSet SetOf(string recoveryFile, string baseDirectory, IReadOnlyList<(string Path, long Size)> files, string chosenFolder) =>
        new(recoveryFile, baseDirectory, [.. files.Select(file => file.Path)], files.Sum(file => file.Size))
        {
            Where = WhereOf(baseDirectory, chosenFolder),
        };

    /// <summary><paramref name="folder"/>, from <paramref name="chosenFolder"/> holding it: "2019\07 - Viagem Bahia".</summary>
    internal static string WhereOf(string folder, string chosenFolder)
    {
        var below = Path.GetRelativePath(chosenFolder, folder);
        return below == "." ? Selection.FolderName(chosenFolder) : Path.Combine(Selection.FolderName(chosenFolder), below);
    }

    private static string? ProblemOf(IReadOnlyList<PlannedSet> sets, int maxFiles)
    {
        var limit = maxFiles.ToString("N0", PtBrazil.Culture);
        return sets switch
        {
            [var only] when only.Files.Count > maxFiles =>
                $"Um conjunto protege até {limit} arquivos, e foram escolhidos {only.Files.Count.ToString("N0", PtBrazil.Culture)}.",
            _ when sets.FirstOrDefault(set => set.Files.Count > maxFiles) is { } tooBig =>
                $"Um conjunto protege até {limit} arquivos, e o conjunto em \"{Selection.FolderName(tooBig.BaseDirectory)}\" teria {tooBig.Files.Count.ToString("N0", PtBrazil.Culture)}.",
            _ => null,
        };
    }
}
