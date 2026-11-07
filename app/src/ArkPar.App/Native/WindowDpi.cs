// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace ArkPar.App.Native;

/// <summary>The screen scale a window is shown at.</summary>
internal static class WindowDpi
{
    /// <summary>The window's DPI: 96 at 100% scale, 144 at 150%.</summary>
    public static uint Of(IntPtr window) => GetDpiForWindow(window);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetDpiForWindow(IntPtr window);
}
