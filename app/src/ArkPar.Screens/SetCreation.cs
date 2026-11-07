// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;

namespace ArkPar.Screens;

/// <summary>How making one set ended.</summary>
internal enum SetEnd
{
    Created,

    /// <summary>Canceled, or never started because the create was canceled first.</summary>
    Canceled,
    Failed,
}

/// <summary>How making one set ended, and what to tell the user about it.</summary>
/// <param name="Note">What happened to the set it replaced, and to files left half written, in the user's words.</param>
/// <param name="Details">What went wrong, in technical terms, if anything did.</param>
internal sealed record SetResult(SetEnd End, string? Note, string? Details)
{
    /// <summary>For a failure: the headline, e.g. "O motor não conseguiu criar os arquivos de recuperação.".</summary>
    public string? Title { get; init; }

    /// <summary>For a failure: what the user can check.</summary>
    public string? Hint { get; init; }

    /// <summary>For a set made: how many files it has, its index and its recovery files.</summary>
    public int FileCount { get; init; }

    /// <summary>For a set made: the size of those files in bytes; null when it could not be read.</summary>
    public long? FilesSize { get; init; }
}

/// <summary>
/// Makes one recovery set: moves the set of the same name out of the way (to the Recycle Bin, or aside on a
/// drive without one), runs the engine, then removes what it left half written if it did not finish, and
/// gives back or deletes the set it replaced. Every failure ends in a <see cref="SetResult"/>, not thrown.
/// </summary>
internal sealed class SetCreation(
    EngineCommand.Create create,
    bool hasRecycleBin,
    Func<EngineCommand, IProgress<EngineProgress>, IEngineRun> startEngine,
    Action<IReadOnlyList<string>> recycle)
{
    /// <summary>Added to the names of a replaced set while it waits aside (on a drive without a Recycle Bin).</summary>
    internal const string AsideSuffix = ".anterior";

    /// <summary>How long a killed engine gets to let go of its files before the partial ones are removed.</summary>
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    // What this create did with the set it replaces: sent it to the Recycle Bin, or set it aside.
    private bool _replacedToRecycleBin;
    private List<(string Original, string Aside)>? _setAside;
    private IEngineRun? _run;

    /// <summary>The set's files already on disk, e.g. of a set of the same name made before: creating replaces them.</summary>
    public static IReadOnlyList<string> Replaced(PlannedSet set) => Selection.RecoverySetFiles(set.RecoveryFile);

    /// <param name="replaced">The set this one replaces (<see cref="Replaced"/>), read just before.</param>
    /// <param name="mayStart">Whether the engine may still start, once the replaced set is out of the way: not after Cancel, or once the window closed.</param>
    /// <param name="runChanged">The engine task under way, once started, and null once it ended.</param>
    /// <param name="progress">The engine's progress.</param>
    public async Task<SetResult> CreateAsync(
        IReadOnlyList<string> replaced, Func<bool> mayStart, Action<IEngineRun?> runChanged, IProgress<EngineProgress> progress)
    {
        IEngineRun run;
        try
        {
            // The engine would write over the index but leave the replaced set's recovery files beside the new ones.
            if (replaced.Count > 0 && hasRecycleBin)
            {
                await Task.Run(() => recycle(replaced)).ConfigureAwait(true);
                _replacedToRecycleBin = true;
            }
            else if (replaced.Count > 0)
            {
                // No Recycle Bin on this drive: the set waits aside, so a create that does not finish loses nothing.
                _setAside = await Task.Run(() => SetAside(replaced)).ConfigureAwait(true);
            }

            if (!mayStart())
            {
                // Canceled, or the window closed, meanwhile: start nothing, and give the set back.
                var (replacedNote, restoreFailure) = await SettleReplacedSetAsync(created: false).ConfigureAwait(true);
                return new SetResult(SetEnd.Canceled, replacedNote, restoreFailure);
            }

            run = startEngine(create, progress);
            _run = run;
            runChanged(run);
        }
        catch (Exception failure)
        {
            var movingAway = replaced.Count > 0 && !_replacedToRecycleBin && _setAside is null;
            var (note, settleFailure) = await SettleReplacedSetAsync(created: false).ConfigureAwait(true);
            return movingAway
                ? new SetResult(SetEnd.Failed, note, Join(failure.ToString(), settleFailure))
                {
                    Title = "Não foi possível tirar o conjunto anterior da pasta.",
                    Hint = "Confira se algum dos arquivos dele está aberto em outro programa.",
                }
                : new SetResult(SetEnd.Failed, note, Join(failure.ToString(), settleFailure))
                {
                    Title = "Não foi possível iniciar a criação.",
                    Hint = "O ArkPar pode estar instalado de forma incompleta.",
                };
        }

        EngineResult result;
        try
        {
            result = await run.Completion.ConfigureAwait(true);
        }
        catch (Exception failure)
        {
            // Never leave the screen stuck on "Criando": whatever failed is shown, with its details.
            EndRun(runChanged);
            var leftover = await RemovePartialFilesAsync().ConfigureAwait(true);
            var (note, settleFailure) = await SettleReplacedSetAsync(created: false).ConfigureAwait(true);
            return new SetResult(SetEnd.Failed, Join(note, LeftoverText(leftover), " "), Join(Join(failure.ToString(), leftover), settleFailure))
            {
                Title = "A criação terminou de forma inesperada.",
            };
        }

        EndRun(runChanged);
        return await ResultOfAsync(result).ConfigureAwait(true);
    }

    /// <summary>
    /// Ends the engine at once if it is still running, e.g. when the window closes, and removes the recovery
    /// files it left half written: ended this way, the engine does not remove them itself.
    /// </summary>
    public void Kill()
    {
        if (_run is not { } run)
        {
            return;
        }

        run.Dispose();

        // The run may end with an error of its own; the partial files go either way, so only its end matters.
        if (run.Completion.ContinueWith(_ => { }, TaskScheduler.Default).Wait(KillWait))
        {
            DeletePartialFiles();
            RestoreSetAside();
        }
    }

    private void EndRun(Action<IEngineRun?> runChanged)
    {
        _run?.Dispose();
        _run = null;
        runChanged(null);
    }

    private async Task<SetResult> ResultOfAsync(EngineResult result)
    {
        if (result.Outcome == EngineOutcome.Completed)
        {
            // The set is made: nothing from here on may remove it, and a failure here only changes what is shown.
            var (note, settleFailure) = await SettleReplacedSetAsync(created: true).ConfigureAwait(true);
            try
            {
                var recoveryFiles = Selection.RecoverySetFiles(create.RecoveryFile);
                var size = await Task.Run(() => recoveryFiles.Sum(file => new FileInfo(file).Length)).ConfigureAwait(true);
                return new SetResult(SetEnd.Created, note, settleFailure) { FileCount = recoveryFiles.Count, FilesSize = size };
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // A recovery file moved or locked right after it was made (an antivirus...): the set is still there.
                return new SetResult(SetEnd.Created, note, Join(failure.ToString(), settleFailure));
            }
        }

        var leftover = await RemovePartialFilesAsync().ConfigureAwait(true);
        var (replacedNote, restoreFailure) = await SettleReplacedSetAsync(created: false).ConfigureAwait(true);
        var notes = Join(replacedNote, LeftoverText(leftover), " ");
        return result.Outcome == EngineOutcome.Canceled
            ? new SetResult(SetEnd.Canceled, notes, Join(leftover, restoreFailure))
            : new SetResult(SetEnd.Failed, notes, Join(Join(result.Output, leftover), restoreFailure))
            {
                Title = "O motor não conseguiu criar os arquivos de recuperação.",
                Hint = "Confira se há espaço no disco e se a pasta permite gravar.",
            };
    }

    /// <summary>
    /// Ends what this create did with the set it replaced. Made: the set waiting aside is deleted. Not made: it is
    /// given back, or, if it went to the Recycle Bin, the user is told it is there.
    /// </summary>
    /// <returns>What to tell the user about that set, and why a step failed, if one did.</returns>
    private async Task<(string? Note, string? Failure)> SettleReplacedSetAsync(bool created)
    {
        if (_setAside is null)
        {
            return (_replacedToRecycleBin && !created ? "O conjunto anterior está na Lixeira, se quiser recuperá-lo." : null, null);
        }

        try
        {
            await Task.Run(created ? DeleteSetAside : RestoreSetAside).ConfigureAwait(true);
            return (created ? null : "O conjunto anterior continua na pasta.", null);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Said on screen: the set is still on disk, only under another name.
            return (created
                    ? $"O conjunto anterior ficou na pasta com \"{AsideSuffix}\" no fim do nome; pode apagá-lo."
                    : $"O conjunto anterior ficou com \"{AsideSuffix}\" no fim do nome; tire esse final para usá-lo de novo.",
                failure.ToString());
        }
    }

    /// <summary>Renames <paramref name="files"/> aside, all or none: a move that fails puts back the ones already moved.</summary>
    private static List<(string Original, string Aside)> SetAside(IReadOnlyList<string> files)
    {
        var moved = new List<(string Original, string Aside)>();
        try
        {
            foreach (var file in files)
            {
                // Overwrites only what a create that never finished (the app closed hard) left aside before.
                File.Move(file, file + AsideSuffix, overwrite: true);
                moved.Add((file, file + AsideSuffix));
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            foreach (var (original, aside) in moved)
            {
                File.Move(aside, original);
            }

            throw;
        }

        return moved;
    }

    /// <summary>Gives the set waiting aside its names back (the half-written new one is already gone).</summary>
    private void RestoreSetAside()
    {
        if (_setAside is not { } aside)
        {
            return;
        }

        _setAside = null;
        foreach (var (original, asideName) in aside)
        {
            File.Move(asideName, original);
        }
    }

    private void DeleteSetAside()
    {
        if (_setAside is not { } aside)
        {
            return;
        }

        _setAside = null;
        foreach (var (_, asideName) in aside)
        {
            File.Delete(asideName);
        }
    }

    /// <summary>
    /// Removes what a create that did not finish left behind. Every file of the set's name is from this run:
    /// a set of that name it replaced was moved away before it started.
    /// </summary>
    /// <returns>Null when all went; otherwise why some could not, which the screen then shows.</returns>
    private async Task<string?> RemovePartialFilesAsync()
    {
        try
        {
            await Task.Run(DeletePartialFiles).ConfigureAwait(true);
            return null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // A file held by another program (an antivirus scanning it...): the screen says which to delete.
            return failure.ToString();
        }
    }

    private void DeletePartialFiles()
    {
        foreach (var file in Selection.RecoverySetFiles(create.RecoveryFile))
        {
            DeleteOnceReleased(file);
        }
    }

    /// <summary>
    /// Deletes <paramref name="file"/>, waiting up to about a second for Windows to let go of it: right after
    /// the engine is ended at once, a file it was writing can still be held for a moment ("access denied").
    /// </summary>
    private static void DeleteOnceReleased(string file)
    {
        const int Attempts = 20;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Delete(file);
                return;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException && attempt < Attempts)
            {
                Thread.Sleep(50);
            }
        }
    }

    private string? LeftoverText(string? leftover) => leftover is null
        ? null
        : $"Arquivos incompletos ficaram na pasta; apague os que começam com \"{Path.GetFileNameWithoutExtension(create.RecoveryFile)}\" e terminam em .par2.";

    internal static string? Join(string? first, string? second, string separator = "\n\n") =>
        first is null ? second : second is null ? first : first + separator + second;
}
