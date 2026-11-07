// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ArkPar.Engine;

/// <summary>
/// Reads what the engine prints (UTF-8, because the app always passes <c>/uo</c>) and reports
/// each progress update as soon as it arrives.
/// </summary>
/// <remarks>
/// The engine (source/par2j/common2.c) rewrites one console line for progress: it prints
/// <c>"  0.0% : Activity\r"</c> when an activity starts, then <c>" 42.5%\r"</c> for each update,
/// ending each with a bare CR and no line end. Anything else ("120 \r" file counters, " Pause",
/// "Cancel", log lines) is kept in <see cref="Text"/> but is not progress.
/// </remarks>
internal sealed partial class EngineOutputReader(IProgress<EngineProgress>? progress)
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _segment = new();
    private string _activity = "";

    /// <summary>Everything read so far.</summary>
    public string Text => _text.ToString();

    public void Write(ReadOnlySpan<byte> bytes)
    {
        var count = _decoder.GetCharCount(bytes, flush: false);
        Span<char> chars = count <= 1024 ? stackalloc char[count] : new char[count];
        _decoder.GetChars(bytes, chars, flush: false);
        _text.Append(chars);

        foreach (var c in chars)
        {
            if (c is '\r' or '\n')
            {
                ReadSegment(_segment.ToString());
                _segment.Clear();
            }
            else
            {
                _segment.Append(c);
            }
        }
    }

    private void ReadSegment(string segment)
    {
        var match = ProgressPattern().Match(segment);
        if (!match.Success)
        {
            return;
        }

        if (match.Groups["activity"].Success)
        {
            _activity = match.Groups["activity"].Value.Trim('"');
        }

        var percent = double.Parse(match.Groups["percent"].ValueSpan, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        progress?.Report(new EngineProgress(_activity, percent));
    }

    // "%3d.%d%%" optionally followed by " : text" (print_progress, print_progress_text, print_progress_file).
    [GeneratedRegex(@"^ {0,2}(?<percent>\d{1,3}\.\d)%(?: : (?<activity>.+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressPattern();
}
