// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.ExceptionServices;
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

/// <summary>How a screen follows the making of one set, and may still call it off.</summary>
/// <param name="MayStart">Whether the engine may still start: not after Cancel, or once the window closed.</param>
/// <param name="RunChanged">The engine task under way, once started, and null once it ended.</param>
/// <param name="ActivityChanged">What is being done once the engine has ended, in the user's words.</param>
/// <param name="Progress">The engine's progress.</param>
internal sealed record CreationWatch(Func<bool> MayStart, Action<IEngineRun?> RunChanged, Action<string> ActivityChanged, IProgress<EngineProgress> Progress);

/// <summary>
/// Makes one recovery set without putting the set of the same name at risk: the engine writes the new set under
/// a draft name (<c>Viagem.arkpar-novo.par2</c>). Once it is made, the draft is named ready
/// (<c>Viagem.arkpar-pronto.par2</c>), and only then does the set there go (to the Recycle Bin, or deleted on a
/// drive without one) and the new one take its name. A create that fails, is canceled or is ended by the window
/// closing removes only the draft. Every failure ends in a <see cref="SetResult"/>, not thrown.
/// </summary>
internal sealed class SetCreation(
    EngineCommand.Create create,
    bool hasRecycleBin,
    Func<EngineCommand, IProgress<EngineProgress>, IEngineRun> startEngine,
    Action<IReadOnlyList<string>> recycle)
{
    /// <summary>Added to the set's name while the engine writes it: "Viagem" is written as "Viagem.arkpar-novo".</summary>
    internal const string DraftSuffix = ".arkpar-novo";

    /// <summary>
    /// Added to the set's name once the engine made it whole, until it takes the name of the set there: "Viagem"
    /// is "Viagem.arkpar-pronto" then. A draft still named <see cref="DraftSuffix"/> is never whole (WPC-416).
    /// </summary>
    internal const string ReadySuffix = ".arkpar-pronto";

    /// <summary>Whether <paramref name="setName"/> ("Viagem.arkpar-novo", "Viagem.arkpar-pronto") is a draft's: not a set yet.</summary>
    internal static bool IsDraftName(string setName) =>
        setName.EndsWith(DraftSuffix, StringComparison.OrdinalIgnoreCase) || setName.EndsWith(ReadySuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>How long a killed engine gets to let go of its files before the partial ones are removed.</summary>
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    private readonly EngineCommand.Create _draft =
        new(DraftOf(create.RecoveryFile), create.BaseDirectory, create.SourceFiles, create.RedundancyPercent, create.Options);

    /// <summary>The ready draft's index: "Viagem.arkpar-pronto.par2".</summary>
    private readonly string _ready = NamedWith(create.RecoveryFile, ReadySuffix);
    private IEngineRun? _run;

    /// <summary>The set's files already on disk, e.g. of a set of the same name made before: creating replaces them.</summary>
    public static IReadOnlyList<string> Replaced(PlannedSet set) => Selection.RecoverySetFiles(set.RecoveryFile);

    /// <summary>The draft's index for <paramref name="recoveryFile"/>: "Viagem.arkpar-novo.par2" for "Viagem.par2".</summary>
    internal static string DraftOf(string recoveryFile) => NamedWith(recoveryFile, DraftSuffix);

    private static string NamedWith(string recoveryFile, string suffix) =>
        Path.Combine(Path.GetDirectoryName(recoveryFile) ?? "", Path.GetFileNameWithoutExtension(recoveryFile) + suffix + Selection.RecoveryExtension);

    /// <param name="replaces">Whether a set of the same name is there (<see cref="Replaced"/>), read just before.</param>
    /// <param name="watch">How the screen follows it.</param>
    public async Task<SetResult> CreateAsync(bool replaces, CreationWatch watch)
    {
        // Said whenever the set is not made: the set there was never touched.
        var keptNote = replaces ? "O conjunto anterior continua na pasta." : null;
        SetLock? setLock;
        try
        {
            // Taken before the folder is read, so another window's draft is never taken for one left by an earlier
            // create; and at once, not on another thread: without a draft, the engine starts at once.
            setLock = SetLock.TryTake(create.RecoveryFile);
        }
        catch (Exception failure) when (failure.IsFileAccessFailure())
        {
            return new SetResult(SetEnd.Failed, keptNote, failure.ToString())
            {
                Title = "Não foi possível iniciar a criação.",
            };
        }

        // Another window is making this set: its draft is its own, and nothing here is touched.
        if (setLock is null)
        {
            return new SetResult(SetEnd.Failed, null, null)
            {
                Title = "Este conjunto já está sendo criado em outra janela do ArkPar.",
                Hint = "Espere ela terminar e crie de novo.",
            };
        }

        using (setLock)
        {
            return await CreateHeldAsync(keptNote, watch).ConfigureAwait(true);
        }
    }

    /// <summary><see cref="CreateAsync"/>, once this window holds the set's <see cref="SetLock"/>.</summary>
    /// <param name="keptNote">What to say of the set there when the set is not made.</param>
    private async Task<SetResult> CreateHeldAsync(string? keptNote, CreationWatch watch)
    {
        IEngineRun run;
        try
        {
            // A draft already there was left by an earlier create: one the app or the computer went off on, a file
            // held when it was removed, or a whole set whose rename failed. Without one, the engine starts at once.
            if (PartialFiles().Count > 0)
            {
                if (MayBeTheOnlySet())
                {
                    // Never removed here: the user checks it.
                    var readyFile = Path.GetFileName(_ready);
                    return new SetResult(SetEnd.Failed, keptNote, null)
                    {
                        Title = $"Já existe na pasta um conjunto \"{readyFile}\", deixado por uma criação anterior.",
                        Hint = $"Ele pode ser o único conjunto destes arquivos: confira-o em Verificar e reparar, e então apague-o ou tire \"{ReadySuffix}\" do nome dos arquivos dele.",
                    };
                }

                // Pieces of no use (WPC-409): removed, and the set is made. One still held by another program (an
                // antivirus scanning it...) stops the create before the engine starts, with none removed.
                if (await RemovePartialFilesAsync(onlyIfNoneHeld: true).ConfigureAwait(true) is { } held)
                {
                    return new SetResult(SetEnd.Failed, TextJoin.Of(keptNote, LeftoverText(held), " "), held)
                    {
                        Title = "Não foi possível apagar os arquivos incompletos de uma criação anterior.",
                        Hint = "Confira se algum arquivo .par2 da pasta está aberto em outro programa.",
                    };
                }
            }

            if (!watch.MayStart())
            {
                return new SetResult(SetEnd.Canceled, keptNote, null);
            }

            run = startEngine(_draft, watch.Progress);
            _run = run;
            watch.RunChanged(run);
        }
        catch (Exception failure)
        {
            return new SetResult(SetEnd.Failed, keptNote, failure.ToString())
            {
                Title = "Não foi possível iniciar a criação.",
                Hint = failure.IsFileAccessFailure()
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
            EndRun(watch.RunChanged);
            var leftover = await RemovePartialFilesAsync().ConfigureAwait(true);
            return new SetResult(SetEnd.Failed, TextJoin.Of(keptNote, LeftoverText(leftover), " "), TextJoin.Of(failure.ToString(), leftover))
            {
                Title = "A criação terminou de forma inesperada.",
            };
        }

        EndRun(watch.RunChanged);
        if (result.Outcome == EngineOutcome.Completed)
        {
            return await TakeOverAsync(keptNote, watch.ActivityChanged).ConfigureAwait(true);
        }

        var partial = await RemovePartialFilesAsync().ConfigureAwait(true);
        var notes = TextJoin.Of(keptNote, LeftoverText(partial), " ");
        return result.Outcome == EngineOutcome.Canceled
            ? new SetResult(SetEnd.Canceled, notes, partial)
            : new SetResult(SetEnd.Failed, notes, TextJoin.Of(result.Output, partial))
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
            catch (Exception failure) when (failure.IsFileAccessFailure())
            {
                // A partial file held by another program (an antivirus scanning it...): it stays, and the window
                // closes with no warning, as decided in WPC-399. The next create of this set removes it
                // (CreateAsync).
            }
        }
    }

    /// <summary>
    /// Whether the draft left by an earlier create may be the only set of these files: ready, the set it replaced
    /// gone but the rename not done. Never while the set's index is there. Never without the ready draft's index
    /// either: each rename moves it last and puts everything back if it fails, so a ready draft always keeps it. A
    /// draft not yet named ready, even with its index, is never whole: the engine writes the index first.
    /// </summary>
    private bool MayBeTheOnlySet() => File.Exists(_ready) && !File.Exists(create.RecoveryFile);

    /// <summary>The files an earlier create of this set left: its draft's, ready or not.</summary>
    private List<string> PartialFiles() => [.. Selection.RecoverySetFiles(_draft.RecoveryFile), .. Selection.RecoverySetFiles(_ready)];

    private void EndRun(Action<IEngineRun?> runChanged)
    {
        _run?.Dispose();
        _run = null;
        runChanged(null);
    }

    /// <summary>The draft is a whole set: it is named ready, the set there goes, and the draft takes its name.</summary>
    /// <param name="keptNote">What to say of the set there if this stops before it is touched.</param>
    private async Task<SetResult> TakeOverAsync(string? keptNote, Action<string> activityChanged)
    {
        try
        {
            await Task.Run(() => RenameSet(_draft.RecoveryFile, _ready)).ConfigureAwait(true);
        }
        catch (Exception failure) when (failure.IsFileAccessFailure())
        {
            // Nothing touched yet: it ends as a create that failed, with only the draft removed.
            var leftover = await RemovePartialFilesAsync().ConfigureAwait(true);
            return new SetResult(SetEnd.Failed, TextJoin.Of(keptNote, LeftoverText(leftover), " "), TextJoin.Of(failure.ToString(), leftover))
            {
                Title = "Não foi possível guardar o novo conjunto.",
                Hint = "Confira se algum arquivo .par2 da pasta está aberto em outro programa.",
            };
        }

        // From here on nothing removes the new set: a failure keeps it, under the ready draft's name if need be.
        var readyFile = Path.GetFileName(_ready);
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
            return new SetResult(SetEnd.Failed, $"O novo conjunto ficou na pasta como \"{readyFile}\".", failure.ToString())
            {
                Title = "Não foi possível tirar o conjunto anterior da pasta.",
                Hint = "Confira se algum dos arquivos dele está aberto em outro programa.",
            };
        }

        try
        {
            await Task.Run(() => RenameSet(_ready, create.RecoveryFile)).ConfigureAwait(true);
        }
        catch (Exception failure) when (failure.IsFileAccessFailure())
        {
            return new SetResult(SetEnd.Failed, $"O novo conjunto ficou na pasta como \"{readyFile}\"; tire \"{ReadySuffix}\" do nome para usá-lo.", failure.ToString())
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
        catch (Exception failure) when (failure.IsFileAccessFailure())
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
    /// Gives the files of the set whose index is <paramref name="from"/> the name of <paramref name="to"/>, the
    /// index last, so the set is never found half renamed. A rename that fails puts back the ones already done,
    /// and the set stays whole under its own name.
    /// </summary>
    /// <exception cref="FileNotFoundException">
    /// The index is gone: removed by another program, or by an ArkPar of another Windows user on a shared folder,
    /// which <see cref="SetLock"/> does not reach. Never told as a set made.
    /// </exception>
    private static void RenameSet(string from, string to)
    {
        if (!File.Exists(from))
        {
            throw new FileNotFoundException("The set to rename is gone.", from);
        }

        var fromName = Path.GetFileNameWithoutExtension(from);
        var toName = Path.GetFileNameWithoutExtension(to);
        var renamed = new List<(string From, string To)>();
        try
        {
            foreach (var file in Selection.RecoverySetFiles(from).OrderBy(file => file.Equals(from, StringComparison.OrdinalIgnoreCase)))
            {
                var renamedFile = Path.Combine(Path.GetDirectoryName(file) ?? "", toName + Path.GetFileName(file)[fromName.Length..]);
                File.Move(file, renamedFile);
                renamed.Add((file, renamedFile));
            }
        }
        catch (Exception failure) when (failure.IsFileAccessFailure())
        {
            foreach (var (original, renamedFile) in renamed)
            {
                File.Move(renamedFile, original);
            }

            throw;
        }
    }

    /// <summary>Removes what a create that did not finish left behind: every file of the draft's names, and only those.</summary>
    /// <param name="onlyIfNoneHeld">Removes none of them while one is held by another program.</param>
    /// <returns>Null when all went; otherwise why some could not, which the screen then shows.</returns>
    private async Task<string?> RemovePartialFilesAsync(bool onlyIfNoneHeld = false)
    {
        try
        {
            await Task.Run(() =>
            {
                if (onlyIfNoneHeld)
                {
                    // Opened alone, at once: fails on a file another program has open.
                    foreach (var file in PartialFiles())
                    {
                        using var alone = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
                    }
                }

                DeletePartialFiles();
            }).ConfigureAwait(true);
            return null;
        }
        catch (Exception failure) when (failure.IsFileAccessFailure())
        {
            // A file held by another program (an antivirus scanning it...): the screen says which to delete.
            return failure.ToString();
        }
    }

    /// <summary>
    /// Deletes every partial file it can: one held by another program does not keep the others there.
    /// </summary>
    /// <exception cref="IOException">Some files stayed: the one failure as it was, or several inside this one.</exception>
    /// <exception cref="UnauthorizedAccessException">The one file that stayed was refused.</exception>
    private void DeletePartialFiles()
    {
        var failures = new List<Exception>();
        foreach (var file in PartialFiles())
        {
            try
            {
                DeleteOnceReleased(file);
            }
            catch (Exception failure) when (failure.IsFileAccessFailure())
            {
                failures.Add(failure);
            }
        }

        if (failures is [var only])
        {
            ExceptionDispatchInfo.Throw(only);
        }

        if (failures.Count > 1)
        {
            throw new IOException($"{failures.Count} partial files could not be deleted.", new AggregateException(failures));
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
            catch (Exception failure) when (failure.IsFileAccessFailure() && attempt < Attempts)
            {
                Thread.Sleep(50);
            }
        }
    }

    private string? LeftoverText(string? leftover) => leftover is null
        ? null
        // What the draft's names share, ready or not: "Viagem.arkpar-".
        : $"Arquivos incompletos ficaram na pasta; apague os que começam com \"{Path.GetFileNameWithoutExtension(create.RecoveryFile)}.arkpar-\" e terminam em .par2.";
}
