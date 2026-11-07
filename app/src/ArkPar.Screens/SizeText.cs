// SPDX-License-Identifier: GPL-2.0-or-later

namespace ArkPar.Screens;

/// <summary>File sizes as the screens show them: "6,2 MB", "182 MB", never in bytes.</summary>
public static class SizeText
{
    private static readonly string[] Units = ["KB", "MB", "GB", "TB"];

    /// <summary>Like Windows Explorer, in units of 1024: one decimal below 10, none from 10 up.</summary>
    public static string Of(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);

        // Below 1 KB, round up as Explorer does, so a tiny file never shows as "0 KB".
        if (bytes < 1024)
        {
            return bytes == 0 ? "0 KB" : "1 KB";
        }

        var value = bytes / 1024.0;
        var unit = 0;
        while (true)
        {
            var shown = Math.Round(value, value < 10 ? 1 : 0, MidpointRounding.AwayFromZero);
            if (shown < 1024 || unit == Units.Length - 1)
            {
                return shown.ToString(shown < 10 ? "0.0" : "0", PtBrazil.Culture) + " " + Units[unit];
            }

            value /= 1024;
            unit++;
        }
    }
}
