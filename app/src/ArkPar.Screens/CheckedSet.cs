// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;

namespace ArkPar.Screens;

/// <summary>Where a recovery set of a folder verification is.</summary>
public enum SetHealth
{
    Waiting,
    Verifying,
    Complete,
    Repairable,
    Unrepairable,

    /// <summary>The engine could not read the set's recovery file.</summary>
    Unreadable,
    Repairing,
    Repaired,

    /// <summary>The repair ran and did not fix every file.</summary>
    RepairFailed,

    /// <summary>The verification or repair was canceled before it ended, so how the files are is not known.</summary>
    NotVerified,
}

/// <summary>The groups the summary of a folder verification counts folders in.</summary>
public enum HealthGroup
{
    Complete,
    Repairable,
    Unrepairable,

    /// <summary>Not read: the recovery file could not be read, or the verification was canceled first.</summary>
    Unverified,
}

/// <summary>A recovery set of a folder verification, with what its verification or repair found.</summary>
public sealed class CheckedSet : Observable
{
    private SetHealth _health;
    private EngineResult? _verified;
    private int _problems;
    private int _files;

    internal CheckedSet(FoundSet found)
    {
        Found = found;
    }

    public FoundSet Found { get; }

    public string RecoveryFile => Found.RecoveryFile;

    /// <summary>The recovery file's name, e.g. "Praia.par2".</summary>
    public string FileName => Path.GetFileName(Found.RecoveryFile);

    public SetHealth Health
    {
        get => _health;
        private set
        {
            if (Set(ref _health, value))
            {
                Announce(nameof(Group));
                Announce(nameof(HasResult));
                Announce(nameof(HealthText));
            }
        }
    }

    /// <summary>The summary's group; null while waiting, verifying or repairing.</summary>
    public HealthGroup? Group => GroupOf(Health);

    /// <summary>The set was verified or repaired, so its detail can be opened.</summary>
    public bool HasResult => Group is not null;

    /// <summary>What was found, e.g. "2 arquivos com problema · o reparo é possível".</summary>
    public string HealthText => Health switch
    {
        SetHealth.Waiting => "Aguardando",
        SetHealth.Verifying => "Verificando…",
        SetHealth.Complete => _files == 1 ? "1 arquivo, íntegro" : $"{PtBrazil.Count(_files, "arquivo", "arquivos")}, todos íntegros",
        SetHealth.Repairable => WithProblem + " · o reparo é possível",
        SetHealth.Unrepairable => WithProblem + " · o reparo não é possível",
        SetHealth.Unreadable => "O arquivo de recuperação não pôde ser lido",
        SetHealth.Repairing => "Reparando…",
        SetHealth.Repaired => PtBrazil.Count(_problems, "arquivo consertado", "arquivos consertados"),
        SetHealth.RepairFailed => "O reparo não consertou todos os arquivos",
        _ => "Não verificado",
    };

    /// <summary>The verification that found how the set is, to show it again without the engine; null when there is none.</summary>
    public EngineResult? Verified => _verified;

    private string WithProblem => PtBrazil.Count(_problems, "arquivo com problema", "arquivos com problema");

    internal void Start(bool isRepair) => Health = isRepair ? SetHealth.Repairing : SetHealth.Verifying;

    /// <summary>A verification or repair of the set that never ran: canceled before its turn.</summary>
    internal void Skip(SetHealth health) => Health = health;

    /// <summary>Takes in how a verification or repair of the set ended.</summary>
    internal void End(EngineEnded ended)
    {
        var result = ended.Result;
        var isRepair = ended.Command is EngineCommand.Repair;
        if (!isRepair && result.Outcome == EngineOutcome.Canceled && Health != SetHealth.Verifying)
        {
            // A new verification of a set already checked (in its detail), canceled: it changed no file, so what
            // was found before still holds. Only the folder verification's own run leaves a set unverified.
            return;
        }

        if (result.Report is { } report)
        {
            _files = report.SourceFiles.Count;
            _problems = report.SourceFiles.Count - report.CompleteFiles.Count;
        }

        // A repair changes the files: the verification from before it no longer tells how they are.
        _verified = !isRepair && result.Outcome is not EngineOutcome.Canceled ? result : null;
        Health = HealthAfter(ended.Command, result.Outcome);

        // The text depends on the counts too, which may have changed even when the health did not.
        Announce(nameof(HealthText));
        Announce(nameof(Verified));
    }

    /// <summary>How a set is after an engine task on it ended: the one place a result becomes a group of the summary.</summary>
    internal static SetHealth HealthAfter(EngineCommand command, EngineOutcome outcome) => (command, outcome) switch
    {
        (_, EngineOutcome.Canceled) => SetHealth.NotVerified,
        (_, EngineOutcome.Error) => SetHealth.Unreadable,
        (EngineCommand.Repair, EngineOutcome.Completed) => SetHealth.Repaired,
        (EngineCommand.Repair, _) => SetHealth.RepairFailed,
        (_, EngineOutcome.Completed) => SetHealth.Complete,
        (_, EngineOutcome.RepairPossible) => SetHealth.Repairable,
        _ => SetHealth.Unrepairable,
    };

    internal static HealthGroup? GroupOf(SetHealth health) => health switch
    {
        SetHealth.Complete or SetHealth.Repaired => HealthGroup.Complete,
        SetHealth.Repairable => HealthGroup.Repairable,
        SetHealth.Unrepairable or SetHealth.RepairFailed => HealthGroup.Unrepairable,
        SetHealth.Unreadable or SetHealth.NotVerified => HealthGroup.Unverified,
        _ => null,
    };
}
