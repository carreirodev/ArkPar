// SPDX-License-Identifier: GPL-2.0-or-later

namespace ArkPar.Engine;

/// <summary>
/// One progress update from the engine.
/// </summary>
/// <param name="Activity">
/// What the engine is doing, in its own English words (e.g. "Creating recovery slice"),
/// or the name of the file it is reading.
/// </param>
/// <param name="Percent">How far along that activity is, from 0 to 100.</param>
public sealed record EngineProgress(string Activity, double Percent);
