// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

namespace ArkPar.Engine.Native;

/// <summary>
/// Joins arguments into one Windows command line, quoted so that the C runtime's parser
/// (which the engine's <c>wmain</c> uses) splits it back into exactly the same arguments.
/// </summary>
internal static class CommandLine
{
    public static string Join(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));

    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return argument;
        }

        var quoted = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes are literal unless they precede a quote: then each one must be doubled,
            // and the quote itself escaped.
            quoted.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        // Trailing backslashes precede the closing quote, so they are doubled too.
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
