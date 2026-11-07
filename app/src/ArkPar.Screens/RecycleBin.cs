// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.VisualBasic.FileIO;

namespace ArkPar.Screens;

/// <summary>Sends files to the Windows Recycle Bin, so the user can get them back.</summary>
public static class RecycleBin
{
    /// <remarks>
    /// Windows shows its own dialog only if a file cannot be moved (open in another program...), and the move
    /// then fails with an exception. A drive without a Recycle Bin (some USB sticks) deletes the files instead,
    /// as Explorer does there.
    /// </remarks>
    public static void Send(IReadOnlyList<string> files)
    {
        foreach (var file in files)
        {
            FileSystem.DeleteFile(file, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        }
    }
}
