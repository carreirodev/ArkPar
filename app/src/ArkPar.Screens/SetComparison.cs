// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;

namespace ArkPar.Screens;

/// <summary>What is in a planned set's folder now, against what a create would make there.</summary>
public enum SetState
{
    /// <summary>Not compared yet.</summary>
    Comparing,

    /// <summary>No set of this name in its folder: creating makes it.</summary>
    Missing,

    /// <summary>A set of this name is there, but its files changed since (a stale set, GLOSSARY.md): creating remakes it.</summary>
    Stale,

    /// <summary>A set of this name is there and covers the very files, unchanged since.</summary>
    UpToDate,

    /// <summary>A set of this name is there, but it could not be read.</summary>
    Unreadable,
}

/// <summary>
/// How a set already on disk compares with the set a create would make in its place, by the files' sizes and
/// dates alone: nothing is read, so a file damaged in place (same size, same date) counts as unchanged. The full
/// check is Verificar's.
/// </summary>
/// <param name="NewFiles">Files the new set covers that the set there does not.</param>
/// <param name="ChangedFiles">Files both cover whose size differs, or that were written after the set.</param>
/// <param name="RemovedFiles">Files the set there covers that are gone from the disk.</param>
/// <param name="MovedOutFiles">Files the set there covers, still on the disk, that the new set leaves to other sets (or to none).</param>
/// <param name="ListedFiles">How many files the set there covers.</param>
/// <param name="MayRecoverLosses">
/// Whether the set there may be the only way to get back something lost: a file gone, or one whose size changed
/// without being written after the set, which looks like damage rather than an edit.
/// </param>
/// <param name="Details">Why the set there could not be read, in technical terms.</param>
public sealed record SetComparison(
    SetState State,
    int NewFiles = 0,
    int ChangedFiles = 0,
    int RemovedFiles = 0,
    int MovedOutFiles = 0,
    int ListedFiles = 0,
    bool MayRecoverLosses = false,
    string? Details = null)
{
    public static SetComparison Comparing { get; } = new(SetState.Comparing);

    public static SetComparison Missing { get; } = new(SetState.Missing);

    /// <summary>Whether a set of this name is in the folder, so creating replaces it.</summary>
    public bool Replaces => State is SetState.Stale or SetState.UpToDate or SetState.Unreadable;

    /// <summary>
    /// Compares the set there, whose files are <paramref name="setFiles"/> (its index and recovery files), with
    /// <paramref name="planned"/>. Lists it with <paramref name="listSet"/>; reads the rest from the disk off
    /// the calling thread. Every failure ends in <see cref="SetState.Unreadable"/>, but a cancel is thrown.
    /// </summary>
    public static async Task<SetComparison> CompareAsync(
        PlannedSet planned,
        IReadOnlyList<string> setFiles,
        Func<string, CancellationToken, Task<SetListing>> listSet,
        CancellationToken stop)
    {
        if (setFiles.Count == 0)
        {
            return Missing;
        }

        try
        {
            // The index if it is there; otherwise the engine finds the set through any of its recovery files.
            var listed = setFiles.FirstOrDefault(file => file.Equals(planned.RecoveryFile, StringComparison.OrdinalIgnoreCase)) ?? setFiles[0];
            var listing = await listSet(listed, stop).ConfigureAwait(true);
            return await Task.Run(() => Compare(planned, listing, WrittenAt(setFiles)), stop).ConfigureAwait(true);
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            // Shown on the screen: the set stays unchecked, and Detalhes técnicos says why.
            return new SetComparison(SetState.Unreadable, Details: failure is EngineListingException listing ? listing.Output : failure.ToString());
        }
    }

    /// <summary>When the set there was made: its newest file, the index being written last.</summary>
    private static DateTime WrittenAt(IReadOnlyList<string> setFiles) => setFiles.Max(file =>
    {
        var info = new FileInfo(file);
        return info.Exists ? info.LastWriteTimeUtc : throw new FileNotFoundException("A file of the set is gone.", file);
    });

    /// <param name="writtenAt">When the set there was made (UTC).</param>
    internal static SetComparison Compare(PlannedSet planned, SetListing listing, DateTime writtenAt)
    {
        int added = 0, changed = 0, removed = 0, movedOut = 0;
        var mayRecoverLosses = false;
        var plannedFiles = new HashSet<string>(planned.Files, StringComparer.OrdinalIgnoreCase);
        foreach (var file in planned.Files)
        {
            var info = new FileInfo(file);
            if (!listing.FileSizes.TryGetValue(file, out var listedSize))
            {
                added++;
            }
            else if (!info.Exists)
            {
                // Gone since it was chosen.
                removed++;
                mayRecoverLosses = true;
            }
            else if (info.Length != listedSize)
            {
                changed++;
                mayRecoverLosses |= info.LastWriteTimeUtc <= writtenAt;
            }
            else if (info.LastWriteTimeUtc > writtenAt)
            {
                changed++;
            }
        }

        foreach (var file in listing.FileSizes.Keys.Where(file => !plannedFiles.Contains(file)))
        {
            if (File.Exists(file))
            {
                movedOut++;
            }
            else
            {
                removed++;
                mayRecoverLosses = true;
            }
        }

        var state = added + changed + removed + movedOut == 0 ? SetState.UpToDate : SetState.Stale;
        return new SetComparison(state, added, changed, removed, movedOut, listing.FileSizes.Count, mayRecoverLosses);
    }
}
