// SPDX-License-Identifier: GPL-2.0-or-later

namespace ArkPar.Screens;

/// <summary>
/// Finds the recovery sets inside a folder, at any depth, for a folder verification. It reads the folders as the
/// Create screen does: hidden and system entries stay out, and links are not followed. Run it off the screen's
/// thread: a big folder takes a while.
/// </summary>
public static class SetSearch
{
    /// <summary>
    /// The sets inside <paramref name="folder"/>: the folder's own first, by name, then each subfolder's, by
    /// name. A set is verified from its index file or, when the index is gone, from its first recovery file.
    /// A whole drive takes a while: <paramref name="stop"/> ends the search between two folders.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="stop"/> was canceled.</exception>
    public static FoundSets Find(string folder, CancellationToken stop = default)
    {
        var sets = new List<FoundSet>();
        var unreadable = new List<string>();
        Search(new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))), sets, unreadable, stop);
        return new FoundSets(sets, unreadable);
    }

    private static void Search(DirectoryInfo folder, List<FoundSet> sets, List<string> unreadable, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        List<FileSystemInfo> entries;
        try
        {
            entries = [.. folder.EnumerateFileSystemInfos().Where(entry => (entry.Attributes & Selection.Skipped) == 0 && !Selection.IsLink(entry))];
        }
        catch (Exception failure) when (failure is UnauthorizedAccessException or IOException)
        {
            // Said on the screen (Unreadable); the sets in the other folders are still verified.
            unreadable.Add(folder.FullName);
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

        foreach (var subfolder in entries.OfType<DirectoryInfo>().OrderBy(subfolder => subfolder.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Search(subfolder, sets, unreadable, stop);
        }
    }
}

/// <summary>What <see cref="SetSearch.Find"/> found.</summary>
/// <param name="Unreadable">Folders that could not be read (no permission), so their sets are not in the list.</param>
public sealed record FoundSets(IReadOnlyList<FoundSet> Sets, IReadOnlyList<string> Unreadable);

/// <summary>A recovery set found in a folder.</summary>
/// <param name="RecoveryFile">The file the engine verifies it from, in full.</param>
/// <param name="Folder">The folder holding it, in full.</param>
/// <param name="Name">Its name, e.g. "Praia" for "Praia.par2" and "Praia.vol0+10.par2".</param>
public sealed record FoundSet(string RecoveryFile, string Folder, string Name);
