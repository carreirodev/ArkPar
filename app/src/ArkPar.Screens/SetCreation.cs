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
/// <param name="Note">What happened to the set it replaces, and to files left half written, in the user's words.</param>
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
/// Makes one recovery set without putting the set of the same name at risk: the engine writes the new set under
/// a draft name (<c>Viagem.arkpar-novo.par2</c>), and only once it is made does the set there go (to the Recycle
/// Bin, or deleted on a drive without one) and the new one take its name. A create that fails, is canceled or is
/// ended by the window closing removes only the draft. Every failure ends in a <see cref="SetResult"/>, not thrown.
/// </summary>
internal sealed class SetCreation(
    EngineCommand.Create create,
    bool hasRecycleBin,
    Func<EngineCommand, IProgress<EngineProgress>, IEngineRun> startEngine,
    Action<IReadOnlyList<string>> recycle)
{
    /// <summary>Added to the set's name while the engine writes it: "Viagem" is written as "Viagem.arkpar-novo".</summary>
    internal const string DraftSuffix = ".arkpar-novo";

    /// <summary>How long a killed engine gets to let go of its files before the partial ones are removed.</summary>
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    private readonly EngineCommand.Create _draft =
        new(DraftOf(create.RecoveryFile), create.BaseDirectory, create.SourceFiles, create.RedundancyPercent, create.Options);
    private IEngineRun? _run;

    /// <summary>The set's files already on disk, e.g. of a set of the same name made before: creating replaces them.</summary>
    public static IReadOnlyList<string> Replaced(PlannedSet set) => Selection.RecoverySetFiles(set.RecoveryFile);

    /// <summary>The draft's index for <paramref name="recoveryFile"/>: "Viagem.arkpar-novo.par2" for "Viagem.par2".</summary>
    internal static string DraftOf(string recoveryFile) =>
        Path.Combine(Path.GetDirectoryName(recoveryFile) ?? "", Path.GetFileNameWithoutExtension(recoveryFile) + DraftSuffix + Selection.RecoveryExtension);

    /// <param name="replaces">Whether a set of the same name is there (<see cref="Replaced"/>), read just before.</param>
    /// <param name="mayStart">Whether the engine may still start: not after Cancel, or once the window closed.</param>
    /// <param name="runChanged">The engine task under way, once started, and null once it ended.</param>
    /// <param name="activityChanged">What is being done once the engine has ended, in the user's words.</param>
    /// <param name="progress">The engine's progress.</param>
    public async Task<SetResult> CreateAsync(
        bool replaces, Func<bool> mayStart, Action<IEngineRun?> runChanged, Action<string> activityChanged, IProgress<EngineProgress> progress)
    {
        // Said whenever the set is not made: the set there was never touched.
        var keptNote = replaces ? "O conjunto anterior continua na pasta." : null;
        IEngineRun run;
        try
        {
            // A draft already there was left by an earlier create: half written if the app closed hard, or whole if
            // the set it replaced went but the rename did not, when it may be the only set of these files. Never
            // removed here: the user checks it. One folder listing: without a draft, the engine starts at once.
            if (Selection.RecoverySetFiles(_draft.RecoveryFile).Count > 0)
            {
                var draftFile = Path.GetFileName(_draft.RecoveryFile);
                return new SetResult(SetEnd.Failed, keptNote, null)
                {
                    Title = $"Já existe na pasta um conjunto \"{draftFile}\", deixado por uma criação anterior.",
                    Hint = $"Ele pode ser o único conjunto destes arquivos: confira-o em Verificar e reparar, e então apague-o ou tire \"{DraftSuffix}\" do nome dos arquivos dele.",
                };
            }

            if (!mayStart())
            {
                return new SetResult(SetEnd.Canceled, keptNote, null);
            }

            run = startEngine(_draft, progress);
            _run = run;
            runChanged(run);
        }
        catch (Exception failure)
        {
            return new SetResult(SetEnd.Failed, keptNote, failure.ToString())
            {
                Title = "Não foi possível iniciar a criação.",
                Hint = failure is IOException or UnauthorizedAccessException
                    ? "Confira se a pasta permite gravar."
                    : "O ArkPar pode estar instalado de forma incompleta.",
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
            return new SetResult(SetEnd.Failed, Join(keptNote, LeftoverText(leftover), " "), Join(failure.ToString(), leftover))
            {
                Title = "A criação terminou de forma inesperada.",
            };
        }

        EndRun(runChanged);
        if (result.Outcome == EngineOutcome.Completed)
        {
            return await TakeOverAsync(activityChanged).ConfigureAwait(true);
        }

        var partial = await RemovePartialFilesAsync().ConfigureAwait(true);
        var notes = Join(keptNote, LeftoverText(partial), " ");
        return result.Outcome == EngineOutcome.Canceled
            ? new SetResult(SetEnd.Canceled, notes, partial)
            : new SetResult(SetEnd.Failed, notes, Join(result.Output, partial))
            {
                Title = "O motor não conseguiu criar os arquivos de recuperação.",
                Hint = "Confira se há espaço no disco e se a pasta permite gravar.",
            };
    }

    /// <summary>
    /// Ends the engine at once if it is still running, e.g. when the window closes, and removes the draft it left
    /// half written: ended this way, the engine does not remove it itself. The set there is never touched. Never
    /// throws for a partial file that cannot be removed: the window closes all the same.
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
            try
            {
                DeletePartialFiles();
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // A partial file held by another program (an antivirus scanning it...): it stays, and the window
                // closes with no warning, as decided in WPC-399. Not lost: the next create of this set stops on
                // the draft left there and names it (CreateAsync).
            }
        }
    }

    internal static string? Join(string? first, string? second, string separator = "\n\n") =>
        first is null ? second : second is null ? first : first + separator + second;

    private void EndRun(Action<IEngineRun?> runChanged)
    {
        _run?.Dispose();
        _run = null;
        runChanged(null);
    }

    /// <summary>The draft is a whole set: the set there goes, and the draft takes its name.</summary>
    private async Task<SetResult> TakeOverAsync(Action<string> activityChanged)
    {
        // From here on nothing removes the new set: a failure keeps it, under the draft's name if need be.
        var draftFile = Path.GetFileName(_draft.RecoveryFile);
        try
        {
            var replaced = await Task.Run(() => Selection.RecoverySetFiles(create.RecoveryFile)).ConfigureAwait(true);
            if (replaced.Count > 0)
            {
                activityChanged(hasRecycleBin ? "Levando o conjunto anterior para a Lixeira" : "Apagando o conjunto anterior");
                await Task.Run(() => RemoveReplaced(replaced)).ConfigureAwait(true);
            }
        }
        catch (Exception failure)
        {
            // Shown with its details: a file of the set there held by another program, or its removal refused in Windows' dialog.
            return new SetResult(SetEnd.Failed, $"O novo conjunto ficou na pasta como \"{draftFile}\".", failure.ToString())
            {
                Title = "Não foi possível tirar o conjunto anterior da pasta.",
                Hint = "Confira se algum dos arquivos dele está aberto em outro programa.",
            };
        }

        try
        {
            await Task.Run(RenameDraft).ConfigureAwait(true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return new SetResult(SetEnd.Failed, $"O novo conjunto ficou na pasta como \"{draftFile}\"; tire \"{DraftSuffix}\" do nome para usá-lo.", failure.ToString())
            {
                Title = "Não foi possível dar o nome final ao novo conjunto.",
                Hint = "Confira se algum arquivo .par2 da pasta está aberto em outro programa.",
            };
        }

        try
        {
            var recoveryFiles = Selection.RecoverySetFiles(create.RecoveryFile);
            var size = await Task.Run(() => recoveryFiles.Sum(file => new FileInfo(file).Length)).ConfigureAwait(true);
            return new SetResult(SetEnd.Created, null, null) { FileCount = recoveryFiles.Count, FilesSize = size };
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // A recovery file moved or locked right after it was made (an antivirus...): the set is still there.
            return new SetResult(SetEnd.Created, null, failure.ToString());
        }
    }

    private void RemoveReplaced(IReadOnlyList<string> replaced)
    {
        if (hasRecycleBin)
        {
            recycle(replaced);
            return;
        }

        foreach (var file in replaced)
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// Gives the draft's files the set's name, its index last, so the set is never found half renamed. A rename
    /// that fails puts back the ones already done, and the draft stays whole under its own name.
    /// </summary>
    private void RenameDraft()
    {
        var draftName = Path.GetFileNameWithoutExtension(_draft.RecoveryFile);
        var name = Path.GetFileNameWithoutExtension(create.RecoveryFile);
        var renamed = new List<(string Draft, string Final)>();
        try
        {
            foreach (var file in Selection.RecoverySetFiles(_draft.RecoveryFile).OrderBy(file => file.Equals(_draft.RecoveryFile, StringComparison.OrdinalIgnoreCase)))
            {
                var final = Path.Combine(Path.GetDirectoryName(file) ?? "", name + Path.GetFileName(file)[draftName.Length..]);
                File.Move(file, final);
                renamed.Add((file, final));
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            foreach (var (draft, final) in renamed)
            {
                File.Move(final, draft);
            }

            throw;
        }
    }

    /// <summary>Removes what a create that did not finish left behind: every file of the draft's name, and only those.</summary>
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
        foreach (var file in Selection.RecoverySetFiles(_draft.RecoveryFile))
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
        : $"Arquivos incompletos ficaram na pasta; apague os que começam com \"{Path.GetFileNameWithoutExtension(_draft.RecoveryFile)}\" e terminam em .par2.";
}
