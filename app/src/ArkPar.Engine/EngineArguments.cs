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

    // Default CPU threads (0) + 256: use the GPU when there is one. Without a usable GPU the engine
    // falls back to the CPU by itself (rs_encode.c and rs_decode.c, after init_OpenCL fails).
    internal const string CpuAndGpu = "/lc256";

    public static IReadOnlyList<string> For(EngineCommand command, string runDirectory) => command switch
    {
        // The photos go in a list file, not on the command line, which Windows caps at 32,767 characters.
        EngineCommand.Create create =>
        [
            "c", Utf8Output, CpuAndGpu,
            "/rr" + create.RedundancyPercent.ToString("0.##", CultureInfo.InvariantCulture),
            "/d" + create.BaseDirectory,
            "/fu", create.RecoveryFile, Path.Combine(runDirectory, SourceListName),
        ],

        // The JSON result goes in the run folder: by default the engine writes it next to itself,
        // which is read-only once the app is installed.
        EngineCommand.Verify verify => ["v", Utf8Output, CpuAndGpu, "/vd" + runDirectory, "/w", verify.RecoveryFile],
        EngineCommand.Repair repair => ["r", Utf8Output, CpuAndGpu, "/vd" + runDirectory, "/w", repair.RecoveryFile],
        EngineCommand.List list => ["l", Utf8Output, list.RecoveryFile],
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown engine command."),
    };

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
