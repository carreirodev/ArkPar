// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text.RegularExpressions;

namespace ArkPar.Engine;

/// <summary>The files a recovery set protects, as the engine's list (<c>l</c>) prints them. Every path is a full Windows path.</summary>
/// <param name="BaseDirectory">The folder the set protects (the folder of its recovery files).</param>
/// <param name="FileSizes">Each file the set protects, with the size the set recorded, compared ignoring case.</param>
public sealed partial record SetListing(string BaseDirectory, IReadOnlyDictionary<string, long> FileSizes)
{
    /// <summary>Reads what the engine printed (par2.c).</summary>
    /// <exception cref="FormatException">The output lacks the set's folder, or names fewer files than the set has.</exception>
    public static SetListing Parse(string engineOutput)
    {
        var lines = VerificationReport.Lines(engineOutput);
        // Printed with a trailing '\', which a drive's root ("P:\") keeps.
        var baseDirectory = lines.Select(line => BaseDirectoryLine().Match(line)).FirstOrDefault(match => match.Success) is { } match
            ? Path.TrimEndingDirectorySeparator(match.Groups["path"].Value)
            : throw new FormatException("The engine's list has no 'Base Directory'.");

        var sizes = VerificationReport.FileSizesIn(engineOutput, name => Path.Combine(baseDirectory, VerificationReport.ToWindowsPath(name)));
        var count = lines.Select(line => FileCountLine().Match(line)).FirstOrDefault(match => match.Success) is { } countMatch
            ? int.Parse(countMatch.Groups["count"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture)
            : throw new FormatException("The engine's list has no 'Input File total count'.");

        // A file the engine could not name is missing from the list: the set's files would be wrongly counted.
        return sizes.Count == count
            ? new SetListing(baseDirectory, sizes)
            : throw new FormatException($"The engine's list names {sizes.Count} of the set's {count} files.");
    }

    [GeneratedRegex(@"^Base Directory\t: ""(?<path>.+)""$", RegexOptions.CultureInvariant)]
    private static partial Regex BaseDirectoryLine();

    [GeneratedRegex(@"^Input File total count\t: (?<count>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex FileCountLine();
}
