// SPDX-License-Identifier: GPL-2.0-or-later

namespace ArkPar.Screens;

/// <summary>Tells the failures a screen expects from the disk from the ones it does not.</summary>
internal static class FileFailure
{
    /// <summary>
    /// Whether <paramref name="failure"/> is Windows refusing a file or a folder: gone, held by another program,
    /// no permission, the disk full or unplugged. The failures a screen expects from the disk, and tells the user.
    /// </summary>
    public static bool IsFileAccessFailure(this Exception failure) => failure is IOException or UnauthorizedAccessException;
}
