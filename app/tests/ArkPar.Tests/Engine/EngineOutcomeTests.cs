// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;

namespace ArkPar.Tests.Engine;

/// <summary>
/// Exit codes from source/par2j/Command_par2j.txt. The ones marked "captured" were returned by the
/// real engine in the runs saved under Fixtures/.
/// </summary>
[TestClass]
public sealed class EngineOutcomeTests
{
    [TestMethod]
    [DataRow(0, DisplayName = "0: created, listed or verified complete (captured)")]
    [DataRow(16, DisplayName = "16: repair succeeded (captured)")]
    [DataRow(256, DisplayName = "256: photos complete, a recovery file is damaged")]
    [DataRow(256 | 16, DisplayName = "272: repaired, a recovery file is damaged")]
    public void Completed(int exitCode) => Assert.AreEqual(EngineOutcome.Completed, EngineOutcomes.FromExitCode(exitCode));

    [TestMethod]
    [DataRow(2, DisplayName = "2: canceled by the c key (captured)")]
    public void Canceled(int exitCode) => Assert.AreEqual(EngineOutcome.Canceled, EngineOutcomes.FromExitCode(exitCode));

    [TestMethod]
    [DataRow(128 | 32 | 4, DisplayName = "164: rename and repair possible (captured)")]
    [DataRow(128 | 4, DisplayName = "132: repair possible (captured)")]
    [DataRow(32 | 4, DisplayName = "36: only a rename is needed")]
    [DataRow(64 | 4, DisplayName = "68: rejoin possible")]
    [DataRow(128 | 8 | 4, DisplayName = "140: repair may be possible (just enough recovery data)")]
    [DataRow(256 | 128 | 4, DisplayName = "388: repair possible, a recovery file is damaged")]
    public void RepairPossible(int exitCode) => Assert.AreEqual(EngineOutcome.RepairPossible, EngineOutcomes.FromExitCode(exitCode));

    [TestMethod]
    [DataRow(8 | 4, DisplayName = "12: needs more recovery data (captured)")]
    [DataRow(16 | 4, DisplayName = "20: repair failed")]
    [DataRow(4, DisplayName = "4: incomplete, nothing can repair it")]
    public void RepairImpossible(int exitCode) => Assert.AreEqual(EngineOutcome.RepairImpossible, EngineOutcomes.FromExitCode(exitCode));

    [TestMethod]
    [DataRow(1, DisplayName = "1: fatal error")]
    [DataRow(1 | 4, DisplayName = "5: fatal error wins over the other bits")]
    [DataRow(-1, DisplayName = "-1: not an engine code")]
    public void Error(int exitCode) => Assert.AreEqual(EngineOutcome.Error, EngineOutcomes.FromExitCode(exitCode));
}
