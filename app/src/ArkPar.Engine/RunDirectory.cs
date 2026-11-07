// SPDX-License-Identifier: GPL-2.0-or-later

namespace ArkPar.Engine;

/// <summary>
/// A fresh temporary folder per engine task, for the file list and the JSON result,
/// so a result is never one from an earlier task.
/// </summary>
internal static class RunDirectory
{
    public static string Create()
    {
        var path = Path.Combine(Path.GetTempPath(), "ArkPar", "runs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
