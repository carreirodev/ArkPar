// SPDX-License-Identifier: GPL-2.0-or-later

namespace ArkPar.Screens;

/// <summary>
/// An orphaned set (GLOSSARY.md), shown as a warning on the list the Create screen shows before creating: the
/// create never makes nor remakes it.
/// </summary>
/// <param name="Name">What the row is named: "IMG_1101.JPG.par2", or a folder's name when the folder could not be read.</param>
/// <param name="Path">The set's index file, in full, or the folder.</param>
/// <param name="Where">Its folder, from the folder holding what was chosen: "2026\Novembro".</param>
/// <param name="StatusText">What happened to it and what to do, e.g. "1 arquivo removido, use Verificar e reparar".</param>
/// <param name="Details">Why it could not be read, in technical terms.</param>
public sealed record OrphanedSet(string Name, string Path, string Where, string StatusText, string? Details = null);
