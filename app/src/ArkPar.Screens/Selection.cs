// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.RegularExpressions;

namespace ArkPar.Screens;

/// <summary>
/// What a single set covers: the files and folders chosen on the Create screen, read from disk, with every
/// file inside the folders at any depth. Read it off the screen's thread: a big folder takes a while.
/// </summary>
public sealed partial class Selection
{
    /// <summary>The most files one recovery set can protect (the PAR2 format's limit, enforced by the engine).</summary>
    public const int MaxFiles = 32768;

    private const string RecoveryExtension = ".par2";

    // Hidden and system files (Thumbs.db, desktop.ini) stay out, as in the engine's own folder search.
    private const FileAttributes Skipped = FileAttributes.Hidden | FileAttributes.System;

    private Selection(IReadOnlyList<string> roots)
    {
        Roots = roots;
    }

    /// <summary>Nothing chosen yet.</summary>
    public static Selection Empty { get; } = new([]);

    /// <summary>The files and folders chosen, in full, without one inside another.</summary>
    public IReadOnlyList<string> Roots { get; }

    /// <summary>Why no set can be made of this selection, in the user's words; null when one can.</summary>
    public string? Problem { get; private init; }

    /// <summary>The folder the set protects and lives in: the folder chosen, or the one holding everything chosen.</summary>
    public string? BaseDirectory { get; private init; }

    /// <summary>The set's index file, named after what was chosen, directly in <see cref="BaseDirectory"/>.</summary>
    public string? RecoveryFile { get; private init; }

    /// <summary>Every file the set will protect, in full.</summary>
    public IReadOnlyList<string> Files { get; private init; } = [];

    public long TotalSize { get; private init; }

    /// <summary>How many files and how much they hold, e.g. "412 arquivos · 3,1 GB".</summary>
    public string Summary => PtBrazil.Count(Files.Count, "arquivo", "arquivos") + " · " + SizeText.Of(TotalSize);

    /// <summary>What was chosen, as a tree: each folder with its folders, then its files.</summary>
    public IReadOnlyList<SelectionNode> Tree { get; private init; } = [];

    /// <summary>Folders that could not be read (no permission), so their files are not in the set.</summary>
    public IReadOnlyList<string> Unreadable { get; private init; } = [];

    /// <summary>Reads <paramref name="paths"/> from disk. Paths that are not there, and <c>.par2</c> files, are left out.</summary>
    public static Selection Read(IEnumerable<string> paths)
    {
        var fullPaths = paths.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).ToList();
        if (fullPaths.Select(path => Path.GetPathRoot(path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
        {
            return new Selection(fullPaths) { Problem = "Os arquivos de um conjunto precisam estar no mesmo disco." };
        }

        var roots = RootsOf(fullPaths);
        if (roots.Count == 0)
        {
            return Empty;
        }

        var files = new List<string>();
        var unreadable = new List<string>();
        var tree = roots
            .Select(root => Directory.Exists(root)
                ? ReadFolder(new DirectoryInfo(root), files, unreadable) with { StartsExpanded = true }
                : ReadFile(new FileInfo(root), files))
            .OrderBy(node => node.IsFolder ? 0 : 1)
            .ThenBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var baseDirectory = roots is [var only] && Directory.Exists(only) ? only : CommonParent(roots);
        var setName = roots is [var single] && !Directory.Exists(single)
            ? Path.GetFileNameWithoutExtension(single)
            : FolderName(baseDirectory);

        return new Selection(roots)
        {
            BaseDirectory = baseDirectory,
            RecoveryFile = Path.Combine(baseDirectory, setName + RecoveryExtension),
            Files = files,
            TotalSize = tree.Sum(node => node.Size),
            Tree = tree,
            Unreadable = unreadable,
            Problem = files.Count switch
            {
                0 => "Não há arquivos para proteger no que foi escolhido.",
                > MaxFiles => $"Um conjunto protege até {MaxFiles.ToString("N0", PtBrazil.Culture)} arquivos, e foram escolhidos {files.Count.ToString("N0", PtBrazil.Culture)}.",
                _ => null,
            },
        };
    }

    /// <summary>The recovery files already in <see cref="BaseDirectory"/> under this set's name.</summary>
    public IReadOnlyList<string> ExistingRecoveryFiles() => RecoveryFile is null ? [] : RecoverySetFiles(RecoveryFile);

    /// <summary>The files of the set whose index is <paramref name="recoveryFile"/> that are on disk: the index and the files holding the recovery blocks.</summary>
    public static IReadOnlyList<string> RecoverySetFiles(string recoveryFile)
    {
        var folder = Path.GetDirectoryName(recoveryFile);
        if (folder is null || !Directory.Exists(folder))
        {
            return [];
        }

        // "Viagem.par2" and "Viagem.vol0+19.par2" (first block + block count, create.c), not "Viagem 2.par2"
        // nor "Viagem.vol2.par2", which belong to other sets.
        var name = Path.GetFileNameWithoutExtension(recoveryFile);
        return
        [
            .. Directory.EnumerateFiles(folder, "*" + RecoveryExtension)
                .Where(file =>
                {
                    var fileName = Path.GetFileName(file);
                    return fileName.Equals(name + RecoveryExtension, StringComparison.OrdinalIgnoreCase)
                        || (fileName.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                            && RecoveryFileSuffix().IsMatch(fileName.AsSpan(name.Length)));
                })
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
    }

    [GeneratedRegex(@"^\.vol\d+\+\d+\.par2$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RecoveryFileSuffix();

    private static List<string> RootsOf(List<string> fullPaths)
    {
        var existing = fullPaths
            .Where(path => Directory.Exists(path) || (File.Exists(path) && !IsRecoveryFile(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A folder or file inside another chosen folder is already covered by it.
        return [.. existing.Where(path => !existing.Any(other => IsInside(path, other)))];
    }

    private static SelectionNode ReadFile(FileInfo file, List<string> files)
    {
        files.Add(file.FullName);
        return SelectionNode.File(file.Name, file.Length);
    }

    private static SelectionNode ReadFolder(DirectoryInfo folder, List<string> files, List<string> unreadable)
    {
        var folders = new List<SelectionNode>();
        var folderFiles = new List<SelectionNode>();
        try
        {
            foreach (var entry in folder.EnumerateFileSystemInfos().Where(entry => (entry.Attributes & Skipped) == 0 && !IsLink(entry)))
            {
                if (entry is DirectoryInfo subfolder)
                {
                    var node = ReadFolder(subfolder, files, unreadable);
                    if (node.FileCount > 0)
                    {
                        folders.Add(node);
                    }
                }
                else if (!IsRecoveryFile(entry.Name))
                {
                    folderFiles.Add(ReadFile((FileInfo)entry, files));
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Reported on the screen (Unreadable), and the set covers the rest.
            unreadable.Add(folder.FullName);
        }

        IReadOnlyList<SelectionNode> children =
        [
            .. folders.OrderBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase),
            .. folderFiles.OrderBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase),
        ];
        return SelectionNode.Folder(FolderName(folder.FullName), children);
    }

    /// <summary>The nearest folder holding all of <paramref name="roots"/> (full paths on one drive).</summary>
    internal static string CommonParent(List<string> roots)
    {
        var common = Path.GetDirectoryName(roots[0]) ?? roots[0];
        while (!roots.All(root => IsInside(root, common)))
        {
            // Above the root of the drive there is nothing: the roots were not all on one drive.
            common = Path.GetDirectoryName(common)
                ?? throw new ArgumentException("The paths are not all on one drive.", nameof(roots));
        }

        return common;
    }

    /// <summary>Whether <paramref name="path"/> is somewhere inside <paramref name="folder"/>, which may be a drive root ("D:\").</summary>
    internal static bool IsInside(string path, string folder)
    {
        // A drive root keeps its separator ("D:\"); any other folder does not have one to keep.
        var prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A folder's name; for the root of a drive, its letter ("D:\" → "D").</summary>
    internal static string FolderName(string folder) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name : folder.TrimEnd('\\', ':');

    /// <summary>
    /// A symbolic link or junction: not followed, so a link back up the tree never loops. OneDrive's folders
    /// and files are reparse points too, but not links (no target), and they are read like any other.
    /// </summary>
    private static bool IsLink(FileSystemInfo entry) =>
        (entry.Attributes & FileAttributes.ReparsePoint) != 0 && entry.LinkTarget is not null;

    private static bool IsRecoveryFile(string path) => path.EndsWith(RecoveryExtension, StringComparison.OrdinalIgnoreCase);

}

/// <summary>A folder or file in the tree of what a set covers.</summary>
/// <param name="FileCount">How many files it holds, at any depth (1 for a file).</param>
/// <param name="Size">How much they hold, in bytes.</param>
/// <param name="Meta">What the tree shows beside the name: "374 · 2,4 GB" for a folder, "6,2 MB" for a file.</param>
public sealed record SelectionNode(string Name, bool IsFolder, int FileCount, long Size, string Meta, IReadOnlyList<SelectionNode> Children)
{
    /// <summary>Shown open at first: the folders chosen, so what is in them shows; the folders inside start closed.</summary>
    public bool StartsExpanded { get; init; }

    public static SelectionNode File(string name, long size) => new(name, IsFolder: false, 1, size, SizeText.Of(size), []);

    public static SelectionNode Folder(string name, IReadOnlyList<SelectionNode> children)
    {
        var fileCount = children.Sum(child => child.FileCount);
        var size = children.Sum(child => child.Size);
        return new(name, IsFolder: true, fileCount, size, fileCount.ToString("N0", PtBrazil.Culture) + " · " + SizeText.Of(size), children);
    }
}
