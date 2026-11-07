// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace ArkPar.App.Native;

/// <summary>Which window the user is looking at.</summary>
internal static class Foreground
{
    /// <summary>
    /// The window is the one in front, taking the keyboard. Never true while it is minimized or another app is in
    /// front. Asked at the moment, so it cannot lag behind a minimize the way activation events can.
    /// </summary>
    public static bool Is(IntPtr window) => GetForegroundWindow() == window;

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetForegroundWindow();
}
