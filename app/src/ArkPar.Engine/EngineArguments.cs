// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text;

namespace ArkPar.Engine;

/// <summary>
/// The engine's command-line arguments for each <see cref="EngineCommand"/>
/// (options in source/par2j/Command_par2j.txt).
/// </summary>
internal static class EngineArguments
{
    /// <summary>Name of the photo list a create reads, inside the run folder.</summary>
    public const string SourceListName = "files.txt";

    // Output in UTF-8, so photo names with accents arrive intact.
    private const string Utf8Output = "/uo";

    private const string RecycleDamagedFiles = "/br";

    /// <param name="processing">The GPU and threads the task may use; null for <see cref="EngineProcessing.Default"/>.</param>
    public static IReadOnlyList<string> For(EngineCommand command, string runDirectory, EngineProcessing? processing = null)
    {
        var limits = (processing ?? EngineProcessing.Default).Argument;
        return command switch
        {
            // The photos go in a list file, not on the command line, which Windows caps at 32,767 characters.
            EngineCommand.Create create => ["c", .. CreateArguments(create, runDirectory, processing)],

            // The JSON result goes in the run folder: by default the engine writes it next to itself,
            // which is read-only once the app is installed.
            EngineCommand.Verify verify => ["v", Utf8Output, limits, "/vd" + runDirectory, "/w", verify.RecoveryFile],
            // /br: each damaged photo a repair replaces goes to the Recycle Bin, so a failed repair loses nothing
            // (the engine's manual calls a backup "important, because this application may contain bugs").
            EngineCommand.Repair repair => ["r", Utf8Output, limits, "/vd" + runDirectory, "/w", RecycleDamagedFiles, repair.RecoveryFile],
            EngineCommand.List list => ["l", Utf8Output, list.RecoveryFile],
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown engine command."),
        };
    }

    /// <summary>
    /// A dry run of <paramref name="create"/> (<c>t</c>, "trial"): the engine works out the recovery files from
    /// the files' sizes alone, without reading them. Same options as the create, so its sizes are the create's.
    /// </summary>
    public static IReadOnlyList<string> ForTrial(EngineCommand.Create create, string runDirectory, EngineProcessing? processing = null) =>
        ["t", .. CreateArguments(create, runDirectory, processing)];

    private static IEnumerable<string> CreateArguments(EngineCommand.Create create, string runDirectory, EngineProcessing? processing)
    {
        yield return Utf8Output;
        yield return (processing ?? EngineProcessing.Default).Argument;
        yield return "/rr" + create.RedundancyPercent.ToString("0.##", CultureInfo.InvariantCulture);
        if (create.Options?.BlockSize is { } blockSize)
        {
            yield return "/ss" + blockSize.ToString(CultureInfo.InvariantCulture);
        }

        if (create.Options?.RecoveryFileCount is { } recoveryFiles)
        {
            yield return "/rf" + recoveryFiles.ToString(CultureInfo.InvariantCulture);
        }

        if (create.Options?.MemoryEighths is { } memory)
        {
            yield return "/m" + memory.ToString(CultureInfo.InvariantCulture);
        }

        yield return "/d" + create.BaseDirectory;
        yield return "/fu";
        yield return create.RecoveryFile;
        yield return Path.Combine(runDirectory, SourceListName);
    }

    /// <summary>Where verify and repair write their JSON result (json.c: the recovery file's name + ".json").</summary>
    public static string ReportPath(string recoveryFile, string runDirectory) =>
        Path.Combine(runDirectory, Path.GetFileName(recoveryFile) + ".json");

    /// <summary>
    /// Contents of the photo list for <c>/fu</c>: one full path per line, UTF-8 without a byte order
    /// mark (the engine would read the mark as part of the first name).
    /// </summary>
    public static byte[] SourceList(EngineCommand.Create create)
    {
        var list = new StringBuilder();
        foreach (var file in create.SourceFiles)
        {
            list.Append(file).Append("\r\n");
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(list.ToString());
    }
}
