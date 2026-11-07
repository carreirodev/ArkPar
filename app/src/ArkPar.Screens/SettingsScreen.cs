// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security;
using ArkPar.Engine;

namespace ArkPar.Screens;

/// <summary>
/// The Settings screen: the GPU and processor cores every task may use, and whether a double click on a .par2
/// opens ArkPar. Each change is saved at once and holds from the next engine run on.
/// </summary>
/// <remarks>
/// A double click on a .par2 while ArkPar is open opens a second window, in a process of its own. So the file is
/// read again before each change and each engine run: a change made in one window is not undone by the other, and
/// the other's next run uses it.
/// </remarks>
public sealed class SettingsScreen : Observable
{
    private readonly SettingsFile _file;
    private readonly Par2Association _association;
    private readonly Action _associationChanged;
    private AppSettings _settings;
    private Banner? _banner;
    private string? _details;

    /// <param name="file">Where the settings are kept.</param>
    /// <param name="association">The .par2 association of this ArkPar.exe.</param>
    /// <param name="processorCount">The PC's processor threads (<see cref="Environment.ProcessorCount"/>).</param>
    /// <param name="associationChanged">Tells Explorer the association changed (SHChangeNotify in the app).</param>
    public SettingsScreen(SettingsFile file, Par2Association association, int processorCount, Action associationChanged)
    {
        _file = file;
        _association = association;
        _associationChanged = associationChanged;
        _settings = new AppSettings();
        _settings = Latest();

        var most = Math.Max(Math.Min(processorCount, EngineProcessing.MaxThreads), _settings.Threads ?? 1);
        ThreadChoices = [new Choice("Automático", null), .. Enumerable.Range(1, most).Select(count => new Choice(PtBrazil.Count(count, "núcleo", "núcleos"), count))];
    }

    /// <summary>What the engine run starting now may use, as last saved (from this window or another).</summary>
    public EngineProcessing Processing => Latest().Processing;

    public bool UseGpu
    {
        get => _settings.UseGpu;
        set
        {
            if (value != UseGpu)
            {
                TrySave(Latest() with { UseGpu = value });
                Announce(nameof(UseGpu));
            }
        }
    }

    /// <summary>"Automático", then each number of threads from 1 to the PC's (at most what the engine takes).</summary>
    public IReadOnlyList<Choice> ThreadChoices { get; }

    public Choice Threads
    {
        get => ThreadChoices.FirstOrDefault(choice => choice.Value == _settings.Threads) ?? ThreadChoices[0];
        set
        {
            if (value.Value != _settings.Threads)
            {
                TrySave(Latest() with { Threads = value.Value });
                Announce(nameof(Threads));
            }
        }
    }

    /// <summary>The user has not yet said whether a double click on a .par2 should open ArkPar.</summary>
    public bool MustAskAboutPar2Files => _settings.OpenPar2Files is null;

    /// <summary>A double click on a .par2 opens ArkPar, as the user chose.</summary>
    public bool OpenPar2Files
    {
        get => _settings.OpenPar2Files == true;
        set => SetPar2Files(value);
    }

    /// <summary>The user wants ArkPar to open .par2 files, but told Windows to always open them with another app.</summary>
    public bool AnotherAppChosen => OpenPar2Files && _association.AnotherAppChosen;

    /// <summary>What went wrong, or null.</summary>
    public Banner? Banner
    {
        get => _banner;
        private set
        {
            if (Set(ref _banner, value))
            {
                Announce(nameof(HasBanner));
            }
        }
    }

    public bool HasBanner => Banner is not null;

    /// <summary>What went wrong, in technical terms, when something failed; otherwise null.</summary>
    public string? Details { get => _details; private set => Set(ref _details, value); }

    /// <summary>
    /// Reads again what may have changed outside this window: the settings saved by another ArkPar window, and what
    /// Windows was told about .par2 files.
    /// </summary>
    public void Refresh()
    {
        _settings = Latest();
        AnnounceAll();
    }

    /// <summary>Windows refused ArkPar's notifications: the end of each task shows only in its window.</summary>
    public void ShowNotificationsUnavailable(Exception failure)
    {
        Banner = new Banner(BannerKind.Warning, "O Windows não deixou o ArkPar mostrar notificações.", "O fim de cada tarefa aparece só na janela do ArkPar.");
        Details = failure.ToString();
    }

    /// <summary>The answer to the question asked on the first run.</summary>
    public void AnswerPar2Question(bool open) => SetPar2Files(open);

    /// <summary>
    /// When the user chose to open .par2 files with ArkPar, points the double click at this ArkPar.exe again: the app
    /// is a folder the user may have moved since. Called each time the app opens.
    /// </summary>
    public void KeepPar2FilesOpeningHere()
    {
        if (OpenPar2Files && !_association.IsRegistered && ChangeAssociation(open: true) is { } failure)
        {
            ShowAssociationFailure(failure);
        }
    }

    private void SetPar2Files(bool open)
    {
        var before = Latest();
        if (before.OpenPar2Files == open)
        {
            _settings = before;
            AnnouncePar2Files();
            return;
        }

        // The choice is saved before Windows is changed, so a failure to save leaves Windows as the switch shows it.
        if (TrySave(before with { OpenPar2Files = open })
            && (open || before.OpenPar2Files == true)
            && ChangeAssociation(open) is { } failure)
        {
            // Windows kept the old way of opening .par2 files: so does the saved choice.
            TrySave(before);
            ShowAssociationFailure(failure);
        }

        AnnouncePar2Files();
    }

    /// <returns>Null when Windows took the change, otherwise why it did not.</returns>
    private Exception? ChangeAssociation(bool open)
    {
        try
        {
            if (open)
            {
                _association.Register();
            }
            else
            {
                _association.Unregister();
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or SecurityException)
        {
            return failure;
        }

        _associationChanged();
        return null;
    }

    private void ShowAssociationFailure(Exception failure)
    {
        Banner = new Banner(BannerKind.Error, "Não foi possível mudar a forma de abrir os arquivos .par2.", "O Windows não deixou gravar a mudança.");
        Details = failure.ToString();
    }

    /// <summary>Saves <paramref name="next"/>; when that fails, says so and leaves the settings as they were.</summary>
    private bool TrySave(AppSettings next)
    {
        try
        {
            _file.Save(next);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            Banner = new Banner(BannerKind.Error, "Não foi possível salvar as configurações.", "Confira se há espaço no disco e tente de novo.");
            Details = failure.ToString();
            return false;
        }

        _settings = next;
        Banner = null;
        Details = null;
        return true;
    }

    /// <summary>The settings as last saved; when the file cannot be read, says so and keeps the ones in use.</summary>
    private AppSettings Latest()
    {
        try
        {
            return _file.Load();
        }
        catch (InvalidDataException failure)
        {
            // The next change saves a readable file over it.
            Banner = new Banner(BannerKind.Warning, "As configurações salvas não puderam ser lidas e voltaram ao padrão.", Detail: null);
            Details = failure.ToString();
            return _settings;
        }
    }

    private void AnnouncePar2Files()
    {
        Announce(nameof(OpenPar2Files));
        Announce(nameof(MustAskAboutPar2Files));
        Announce(nameof(AnotherAppChosen));
    }

    private void AnnounceAll()
    {
        Announce(nameof(UseGpu));
        Announce(nameof(Threads));
        AnnouncePar2Files();
    }
}
