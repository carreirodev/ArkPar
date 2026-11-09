// SPDX-License-Identifier: GPL-2.0-or-later

namespace ArkPar.Screens;

/// <summary>Puts together texts of which some may be missing: notes of a banner, technical details.</summary>
internal static class TextJoin
{
    /// <summary>Both texts with <paramref name="separator"/> between them, or the one there is; null without any.</summary>
    public static string? Of(string? first, string? second, string separator = "\n\n") =>
        first is null ? second : second is null ? first : first + separator + second;

    /// <summary>Every text there is, in order, with <paramref name="separator"/> between them; null without any.</summary>
    public static string? All(IEnumerable<string?> texts, string separator = "\n\n") =>
        texts.Aggregate((string?)null, (all, text) => Of(all, text, separator));
}
