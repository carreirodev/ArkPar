// SPDX-License-Identifier: GPL-2.0-or-later

namespace ArkPar.Screens;

/// <summary>
/// Finds the recovery sets inside a folder, at any depth, for a folder verification. Hidden and system folders and
/// files are searched too: a set the user hid is still theirs to verify. Only Windows' own folders stay out, and
/// a folder that cannot be opened, or a link to another folder (not followed, so the search never loops), is
/// told as left out. Run it off the screen's thread: a big folder takes a while.
/// </summary>
public static class SetSearch
{
    // The recycle bin and the system restore points, at a drive's root: Windows', not the user's.
    private static readonly string[] WindowsFolders = ["$RECYCLE.BIN", "System Volume Information"];

    /// <summary>
    /// The sets inside <paramref name="folder"/>: the folder's own first, by name, then each subfolder's, by
    /// name. A set is verified from its index file or, when the index is gone, from its first recovery file.
    /// A whole drive takes a while: <paramref name="stop"/> ends the search between two folders.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="stop"/> was canceled.</exception>
    public static FoundSets Find(string folder, CancellationToken stop = default)
    {
        var sets = new List<FoundSet>();
        var leftOut = new List<LeftOutFolder>();
        Search(new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))), sets, leftOut, stop);
        return new FoundSets(sets, leftOut);
    }

    private static void Search(DirectoryInfo folder, List<FoundSet> sets, List<LeftOutFolder> leftOut, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        List<FileSystemInfo> entries;
        try
        {
            entries = [.. folder.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false })];
        }
        catch (Exception failure) when (failure is UnauthorizedAccessException or IOException)
        {
            // Said on the screen as not verified; the sets in the other folders are still verified.
            leftOut.Add(new LeftOutFolder(folder.FullName, LeftOutReason.Unreadable));
            return;
        }

        sets.AddRange(entries
            .OfType<FileInfo>()
            .Select(file => (File: file.FullName, Match: Selection.SetNameOf().Match(file.Name)))
            .Where(file => file.Match.Success)
            .GroupBy(file => file.Match.Groups["name"].Value, StringComparer.OrdinalIgnoreCase)
            .Select(set =>
            {
                var files = set.Select(file => file.File).Order(StringComparer.OrdinalIgnoreCase).ToList();
                var index = Path.Combine(folder.FullName, set.Key + Selection.RecoveryExtension);
                var recoveryFile = files.FirstOrDefault(file => file.Equals(index, StringComparison.OrdinalIgnoreCase)) ?? files[0];
                return new FoundSet(recoveryFile, folder.FullName, set.Key);
            })
            .OrderBy(set => set.Name, StringComparer.CurrentCultureIgnoreCase));

        var subfolders = entries
            .OfType<DirectoryInfo>()
            .Where(subfolder => !WindowsFolders.Contains(subfolder.Name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(subfolder => subfolder.Name, StringComparer.CurrentCultureIgnoreCase);
        foreach (var subfolder in subfolders)
        {
            if (Selection.IsLink(subfolder))
            {
                leftOut.Add(new LeftOutFolder(subfolder.FullName, LeftOutReason.Link));
            }
            else
            {
                Search(subfolder, sets, leftOut, stop);
            }
        }
    }
}

/// <summary>What <see cref="SetSearch.Find"/> found.</summary>
/// <param name="LeftOut">Folders not searched, so any set in them is not in the list, in the order met.</param>
public sealed record FoundSets(IReadOnlyList<FoundSet> Sets, IReadOnlyList<LeftOutFolder> LeftOut);

/// <summary>A recovery set found in a folder.</summary>
/// <param name="RecoveryFile">The file the engine verifies it from, in full.</param>
/// <param name="Folder">The folder holding it, in full.</param>
/// <param name="Name">Its name, e.g. "Praia" for "Praia.par2" and "Praia.vol0+10.par2".</param>
public sealed record FoundSet(string RecoveryFile, string Folder, string Name);

/// <summary>A folder the search did not look into, in full, and why.</summary>
public sealed record LeftOutFolder(string Path, LeftOutReason Reason);

public enum LeftOutReason
{
    /// <summary>Windows would not open it: no permission, or the disk failed.</summary>
    Unreadable,

    /// <summary>A link to another folder (a junction or symbolic link), not followed.</summary>
    Link,
}
