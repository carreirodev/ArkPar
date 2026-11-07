// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArkPar.Engine;

/// <summary>
/// The state of each photo of a recovery set, as the engine found it while verifying or before
/// repairing: the <c>/w</c> JSON file (format in source/par2j/json.c), plus the sizes and recovery
/// block counts only its printed output has. Every path is a full Windows path.
/// </summary>
public sealed partial class VerificationReport
{
    // A JSON key, not this class's property of the same name.
    private const string BaseDirectoryKey = "BaseDirectory";

    private VerificationReport(
        string baseDirectory,
        IReadOnlyList<string> sourceFiles,
        IReadOnlyList<string> damagedFiles,
        IReadOnlyList<string> appendedFiles,
        IReadOnlyList<string> missingFiles,
        IReadOnlyDictionary<string, string> misnamedFiles,
        IReadOnlyList<string> foundFiles,
        IReadOnlyDictionary<string, long> fileSizes,
        RecoveryBlocks? recoveryBlocks)
    {
        BaseDirectory = baseDirectory;
        SourceFiles = sourceFiles;
        DamagedFiles = damagedFiles;
        AppendedFiles = appendedFiles;
        MissingFiles = missingFiles;
        MisnamedFiles = misnamedFiles;
        FoundFiles = foundFiles;
        FileSizes = fileSizes;
        RecoveryBlocks = recoveryBlocks;

        var notComplete = new HashSet<string>(damagedFiles.Concat(appendedFiles).Concat(missingFiles).Concat(misnamedFiles.Keys), StringComparer.OrdinalIgnoreCase);
        CompleteFiles = [.. sourceFiles.Where(file => !notComplete.Contains(file))];
    }

    /// <summary>The folder the recovery set protects.</summary>
    public string BaseDirectory { get; }

    /// <summary>Every photo the recovery set protects.</summary>
    public IReadOnlyList<string> SourceFiles { get; }

    /// <summary>Photos present and intact.</summary>
    public IReadOnlyList<string> CompleteFiles { get; }

    /// <summary>Photos present but with changed content.</summary>
    public IReadOnlyList<string> DamagedFiles { get; }

    /// <summary>Photos intact but with extra data after their end.</summary>
    public IReadOnlyList<string> AppendedFiles { get; }

    /// <summary>Photos not found at all.</summary>
    public IReadOnlyList<string> MissingFiles { get; }

    /// <summary>Photos found intact under another name: original path → path where it was found.</summary>
    public IReadOnlyDictionary<string, string> MisnamedFiles { get; }

    /// <summary>Other files holding parts of the photos (partial copies, files given as extra sources).</summary>
    public IReadOnlyList<string> FoundFiles { get; }

    /// <summary>
    /// The size of each photo as the recovery set recorded it, so also known for a missing photo.
    /// Read from what the engine printed; a photo the engine could not name is left out.
    /// </summary>
    public IReadOnlyDictionary<string, long> FileSizes { get; }

    /// <summary>
    /// Recovery blocks available and needed, as counted before any repair; null when the engine
    /// did not print them (it stopped before counting).
    /// </summary>
    public RecoveryBlocks? RecoveryBlocks { get; }

    /// <param name="jsonPath">The <c>/w</c> JSON file.</param>
    /// <param name="engineOutput">Everything the engine printed, for what the JSON file lacks.</param>
    internal static VerificationReport Load(string jsonPath, string engineOutput)
    {
        // ReadAllText drops a UTF-8 byte order mark, should the engine ever write one.
        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var root = document.RootElement;
        var baseDirectory = ToWindowsPath(root.GetProperty(BaseDirectoryKey).GetString()
            ?? throw new JsonException($"'{BaseDirectoryKey}' is null in '{jsonPath}'."));

        // json.c drops the folder's trailing '\', so a set at the root of a drive comes as "E:",
        // which Windows reads as E:'s current folder. Put the root back.
        if (baseDirectory.EndsWith(':'))
        {
            baseDirectory += '\\';
        }

        string FullPath(string name) => Path.Combine(baseDirectory, ToWindowsPath(name));

        IReadOnlyList<string> Files(string property) =>
            root.TryGetProperty(property, out var files)
                ? [.. files.EnumerateArray().Select(file => FullPath(StringOf(file, property)))]
                : [];

        var misnamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("MisnamedFile", out var misnamedFiles))
        {
            foreach (var pair in misnamedFiles.EnumerateObject())
            {
                misnamed[FullPath(pair.Name)] = FullPath(StringOf(pair.Value, "MisnamedFile"));
            }
        }

        // The engine writes only the lists that have items, and these in this order.
        return new VerificationReport(
            baseDirectory,
            Files("SourceFile"),
            Files("DamagedFile"),
            Files("AppendedFile"),
            Files("MissingFile"),
            misnamed,
            [.. Files("FoundFile"), .. Files("ExternalFile")],
            FileSizesIn(engineOutput, FullPath),
            RecoveryBlocksIn(engineOutput));
    }

    /// <summary>
    /// The "Input File list" the engine prints before verifying (par2.c), one
    /// <c>"      2000000     80 : "viagem\ônibus.jpg""</c> line per photo, up to "Input File total size".
    /// </summary>
    internal static Dictionary<string, long> FileSizesIn(string engineOutput, Func<string, string> fullPath)
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var inList = false;
        foreach (var line in Lines(engineOutput))
        {
            if (line.StartsWith("Input File list", StringComparison.Ordinal))
            {
                inList = true;
            }
            else if (inList && line.StartsWith("Input File total size", StringComparison.Ordinal))
            {
                break;
            }
            else if (inList && FileSizePattern().Match(line) is { Success: true } match)
            {
                sizes[fullPath(match.Groups["name"].Value)] = long.Parse(match.Groups["size"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
            }
        }

        return sizes;
    }

    /// <summary>
    /// "Recovery Slice found" (par2.c) is how many recovery blocks are intact. The first
    /// "Input File Slice lost" (repair.c) is how many blocks of the photos were lost before any
    /// repair, which is how many recovery blocks the repair uses; a repair prints it only once.
    /// </summary>
    private static RecoveryBlocks? RecoveryBlocksIn(string engineOutput)
    {
        int? available = null;
        int? needed = null;
        foreach (var line in Lines(engineOutput))
        {
            available ??= CountAfter(line, "Recovery Slice found\t: ");
            needed ??= CountAfter(line, "Input File Slice lost\t: ");
        }

        return available is { } a && needed is { } n ? new RecoveryBlocks(a, n) : null;
    }

    private static int? CountAfter(string line, string label) =>
        line.StartsWith(label, StringComparison.Ordinal)
            ? int.Parse(line.AsSpan(label.Length), NumberStyles.None, CultureInfo.InvariantCulture)
            : null;

    // Progress updates end with a bare CR, so a CR also ends a line here.
    internal static string[] Lines(string engineOutput) => engineOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    // "%13I64d %6d : \"%s\"" in par2.c; a photo the engine could not name prints "?" instead.
    [GeneratedRegex(@"^ *(?<size>\d+) +\d+ : ""(?<name>.+)""$", RegexOptions.CultureInvariant)]
    private static partial Regex FileSizePattern();

    private static string StringOf(JsonElement element, string property) =>
        element.GetString() ?? throw new JsonException($"A null name in '{property}'.");

    // The engine writes '/' instead of '\' so it never has to escape anything in its JSON.
    internal static string ToWindowsPath(string path) => path.Replace('/', '\\');
}
