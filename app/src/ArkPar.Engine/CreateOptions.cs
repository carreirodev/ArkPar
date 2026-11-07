// SPDX-License-Identifier: GPL-2.0-or-later

namespace ArkPar.Engine;

/// <summary>The advanced settings of a create; each one left null is chosen by the engine.</summary>
/// <param name="BlockSize">
/// Bytes per block (<c>/ss</c>), a multiple of 4. The engine raises a size too small for the files,
/// so the size it really uses is the one in <see cref="RecoveryEstimate.BlockSize"/>.
/// </param>
/// <param name="RecoveryFileCount">How many recovery files hold the recovery blocks (<c>/rf</c>), the index file aside.</param>
/// <param name="MemoryEighths">The most memory the engine may use, in eighths of the free memory, from 1 to 7 (<c>/m</c>).</param>
public sealed record CreateOptions(int? BlockSize = null, int? RecoveryFileCount = null, int? MemoryEighths = null)
{
    /// <summary>Every setting chosen by the engine.</summary>
    public static CreateOptions Automatic { get; } = new();
}
