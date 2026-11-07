// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;

namespace ArkPar.Engine;

/// <summary>
/// The state of each photo of a recovery set, as the engine found it while verifying or before
/// repairing (the <c>/w</c> JSON file; format in source/par2j/json.c). Every path is a full Windows path.
/// </summary>
public sealed class VerificationReport
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
        IReadOnlyList<string> foundFiles)
    {
        BaseDirectory = baseDirectory;
        SourceFiles = sourceFiles;
        DamagedFiles = damagedFiles;
        AppendedFiles = appendedFiles;
        MissingFiles = missingFiles;
        MisnamedFiles = misnamedFiles;
        FoundFiles = foundFiles;

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

    internal static VerificationReport Load(string jsonPath)
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
            [.. Files("FoundFile"), .. Files("ExternalFile")]);
    }

    private static string StringOf(JsonElement element, string property) =>
        element.GetString() ?? throw new JsonException($"A null name in '{property}'.");

    // The engine writes '/' instead of '\' so it never has to escape anything in its JSON.
    private static string ToWindowsPath(string path) => path.Replace('/', '\\');
}
