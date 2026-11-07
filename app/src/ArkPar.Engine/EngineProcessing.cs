// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace ArkPar.Engine;

/// <summary>What the engine may use to calculate: the GPU, and how many processor threads.</summary>
public sealed record EngineProcessing
{
    /// <summary>The most threads the engine takes (<c>/lc</c> in source/par2j/Command_par2j.txt).</summary>
    public const int MaxThreads = 32;

    /// <param name="UseGpu">
    /// Use the GPU when there is one. Without a usable GPU the engine falls back to the CPU by itself
    /// (rs_encode.c and rs_decode.c, after init_OpenCL fails).
    /// </param>
    /// <param name="Threads">From 1 to <see cref="MaxThreads"/>; null lets the engine use one per physical core.</param>
    public EngineProcessing(bool UseGpu = true, int? Threads = null)
    {
        if (Threads is < 1 or > MaxThreads)
        {
            throw new ArgumentOutOfRangeException(nameof(Threads), Threads, $"The engine takes from 1 to {MaxThreads} threads.");
        }

        this.UseGpu = UseGpu;
        this.Threads = Threads;
    }

    /// <summary>The GPU when there is one, and one thread per physical core.</summary>
    public static EngineProcessing Default { get; } = new();

    public bool UseGpu { get; }

    public int? Threads { get; }

    /// <summary>The engine's <c>/lc</c> option: the threads (0 for one per physical core), plus 256 for the GPU.</summary>
    internal string Argument => "/lc" + ((Threads ?? 0) + (UseGpu ? 256 : 0)).ToString(CultureInfo.InvariantCulture);
}
