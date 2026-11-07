// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;

namespace ArkPar.Screens;

/// <summary>Where the Verify and repair screen is.</summary>
public enum VerifyStage
{
    /// <summary>No recovery file chosen yet.</summary>
    Idle,
    Verifying,
    Verified,
    Repairing,
    Repaired,
    Canceled,

    /// <summary>The engine could not verify or repair, or the repair did not fix everything.</summary>
    Failed,
}

public enum BannerKind
{
    Success,
    Warning,
    Error,
}

/// <summary>The headline of a result: what happened and, below it, what it means.</summary>
public sealed record Banner(BannerKind Kind, string Title, string? Detail);

/// <summary>How many files of the set are in each state.</summary>
/// <param name="Misnamed">Intact, but found under another name; the repair gives the name back.</param>
public sealed record FileCounts(int Complete, int Damaged, int Missing, int Misnamed);

public enum ProblemKind
{
    Damaged,
    Missing,
    Misnamed,
    Repaired,
}

/// <summary>A file with a problem, shown as a card.</summary>
/// <param name="Name">Path inside the protected folder, e.g. <c>viagem\ônibus.jpg</c>.</param>
/// <param name="Tag">The label on the card: "Danificado", "Faltando", "Nome trocado" or "Reparado".</param>
/// <param name="ImagePath">The file on disk to take the thumbnail from; null when there is none (missing).</param>
/// <param name="Meta">Size and date, e.g. "6,2 MB · 14/07/2019"; a missing file has no date.</param>
public sealed record ProblemFile(string Name, ProblemKind Kind, string Tag, string? ImagePath, string Meta);

/// <summary>An intact file, shown as a row.</summary>
public sealed record CompleteFile(string Name, string Size, string Date);

/// <summary>An engine task the Verify and repair screen ran, and how it ended.</summary>
public sealed record EngineEnded(EngineCommand Command, EngineResult Result);
