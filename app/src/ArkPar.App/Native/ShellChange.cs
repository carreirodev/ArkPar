// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace ArkPar.App.Native;

/// <summary>Tells Explorer what changed, so it picks it up without a restart.</summary>
internal static class ShellChange
{
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    /// <summary>A file association changed: Explorer reads the associations again (and redraws the icons).</summary>
    public static void NotifyAssociationsChanged() => SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
