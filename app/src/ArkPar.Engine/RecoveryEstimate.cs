// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text.RegularExpressions;

namespace ArkPar.Engine;

/// <summary>What a create would make, as the engine's trial (<c>t</c>) works it out from the files' sizes.</summary>
/// <param name="BlockSize">Bytes per block, as the engine will use it (it raises a size too small for the files).</param>
/// <param name="SourceBlocks">How many blocks the protected files are cut into.</param>
/// <param name="RecoveryBlocks">How many recovery blocks the set will hold.</param>
/// <param name="RecoveryFiles">How many recovery files hold them, the index file aside.</param>
/// <param name="TotalSize">The size of all the recovery files together, index included, in bytes.</param>
public sealed partial record RecoveryEstimate(long BlockSize, int SourceBlocks, int RecoveryBlocks, int RecoveryFiles, long TotalSize)
{
    /// <summary>
    /// The most lost data the set can rebuild, in bytes: one recovery block for each lost block. Damage spread
    /// over many blocks rebuilds less, since each damaged block takes a whole recovery block.
    /// </summary>
    public long RepairableSize => RecoveryBlocks * BlockSize;

    /// <summary>Reads the engine's trial output (par2_cmd.c and par2.c, par2_trial).</summary>
    /// <exception cref="FormatException">The output lacks the sizes, e.g. because the trial failed.</exception>
    public static RecoveryEstimate Parse(string output)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (Match match in ValuePattern().Matches(output))
        {
            values[match.Groups["label"].Value] = long.Parse(match.Groups["value"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        long Value(string label) => values.TryGetValue(label, out var value)
            ? value
            : throw new FormatException($"The engine's trial output has no '{label}'.");

        return new RecoveryEstimate(
            Value("Input File Slice size"),
            checked((int)Value("Input File Slice count")),
            checked((int)Value("Recovery Slice count")),
            checked((int)Value("Recovery File count")),
            Value("PAR File total size"));
    }

    // "Input File Slice size\t: 25000": a label, tabs, a colon and a whole number on a line of its own.
    [GeneratedRegex(@"^(?<label>[A-Za-z][A-Za-z ]*?)\t+: (?<value>\d+)\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ValuePattern();
}
