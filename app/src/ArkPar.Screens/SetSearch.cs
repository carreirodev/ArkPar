// SPDX-License-Identifier: GPL-2.0-or-later

namespace ArkPar.Screens;

/// <summary>
/// Finds the recovery sets inside a folder, at any depth, for a folder verification. Hidden and system folders and
/// files are searched too: a set the user hid is still theirs to verify. Only Windows' own folders and the drafts
/// an interrupted create left half written stay out, and a folder that cannot be opened, or a link to another
/// folder (not followed, so the search never loops), is told as left out. Run it off the screen's thread: a big
/// folder takes a while.
/// </summary>
public static class SetSearch
{
    // The recycle bin and the system restore points, at a drive's root: Windows', not the user's.
    private static readonly string[] WindowsFolders = ["$RECYCLE.BIN", "System Volume Information"];

    /// <summary>
    /// The sets inside <paramref name="folder"/>: the folder's own first, by name, then each subfolder's, by
    /// name. A set is verified from its index file or, when the index is gone, from its first recovery file.
    /// A set lying beside a subfolder of its name ("Praia.par2" beside "Praia", as per subfolder makes them)
    /// protects that subfolder (<see cref="FoundSet.ProtectedFolder"/>).
    /// A whole drive takes a while: <paramref name="stop"/> ends the search between two folders.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="stop"/> was canceled.</exception>
    public static FoundSets Find(string folder, CancellationToken stop = default)
    {
        var sets = new List<FoundSet>();
        var leftOut = new List<LeftOutFolder>();
        var searched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Search(new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))), sets, leftOut, searched, stop);

        // Only a subfolder that was searched: one left out keeps its own line, as not verified.
        return new FoundSets([.. sets.Select(set => UnderItsSubfolder(set, searched))], leftOut);
    }

    /// <summary>
    /// <paramref name="set"/>, summed up under the subfolder of its name beside it when that one is among
    /// <paramref name="searched"/>. A ready draft ("Praia.arkpar-pronto") goes by the name of the set it will be.
    /// A set named as the folder holding it ("2019\2019.par2") is that folder's own, whatever lies beside it.
    /// </summary>
    private static FoundSet UnderItsSubfolder(FoundSet set, HashSet<string> searched)
    {
        var name = set.Name.EndsWith(SetCreation.ReadySuffix, StringComparison.OrdinalIgnoreCase) ? set.Name[..^SetCreation.ReadySuffix.Length] : set.Name;
        return !name.Equals(Selection.FolderName(set.Folder), StringComparison.OrdinalIgnoreCase)
            && searched.TryGetValue(Path.Combine(set.Folder, name), out var subfolder)
            ? set with { ProtectedFolder = subfolder }
            : set;
    }

    private static void Search(DirectoryInfo folder, List<FoundSet> sets, List<LeftOutFolder> leftOut, HashSet<string> searched, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        List<FileSystemInfo> entries;
        try
        {
            entries = [.. folder.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false })];
        }
        catch (Exception failure) when (failure.IsFileAccessFailure())
        {
            // Said on the screen as not verified; the sets in the other folders are still verified.
            leftOut.Add(new LeftOutFolder(folder.FullName, LeftOutReason.Unreadable));
            return;
        }

        searched.Add(folder.FullName);
        sets.AddRange(Selection.SetFilesAmong(entries.OfType<FileInfo>().Select(file => file.FullName))
            // A draft a create left half written ("Viagem.arkpar-novo") is no set: verifying it would tell a problem
            // where the files are protected. A ready draft ("Viagem.arkpar-pronto") is whole, and is verified.
            .Where(set => !SetCreation.IsUnfinishedDraftName(set.Key))
            .Select(set =>
            {
                var index = Path.Combine(folder.FullName, set.Key + Selection.RecoveryExtension);
                var recoveryFile = set.Value.FirstOrDefault(file => file.Equals(index, StringComparison.OrdinalIgnoreCase)) ?? set.Value[0];
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
                Search(subfolder, sets, leftOut, searched, stop);
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
public sealed record FoundSet(string RecoveryFile, string Folder, string Name)
{
    /// <summary>
    /// The folder it is summed up under, in full: the subfolder of its name beside it ("2019\Praia" for
    /// "2019\Praia.par2"), when there is one; otherwise the folder holding it.
    /// </summary>
    public string ProtectedFolder { get; init; } = Folder;
}

/// <summary>A folder the search did not look into, in full, and why.</summary>
public sealed record LeftOutFolder(string Path, LeftOutReason Reason);

public enum LeftOutReason
{
    /// <summary>Windows would not open it: no permission, or the disk failed.</summary>
    Unreadable,

    /// <summary>A link to another folder (a junction or symbolic link), not followed.</summary>
    Link,
}
