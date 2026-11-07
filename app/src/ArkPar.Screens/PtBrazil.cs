// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace ArkPar.Screens;

/// <summary>The language of every text the screens show (Brazilian Portuguese), whatever Windows is set to.</summary>
public static class PtBrazil
{
    public static CultureInfo Culture { get; } = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>A count with its noun, e.g. "1 arquivo", "1.234 arquivos".</summary>
    public static string Count(int count, string singular, string plural) =>
        count == 1 ? "1 " + singular : count.ToString("N0", Culture) + " " + plural;
}
