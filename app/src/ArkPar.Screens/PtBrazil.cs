// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace ArkPar.Screens;

/// <summary>The language of every text the screens show (Brazilian Portuguese), whatever Windows is set to.</summary>
public static class PtBrazil
{
    public static CultureInfo Culture { get; } = CultureInfo.GetCultureInfo("pt-BR");
}
