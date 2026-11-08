// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.App.Native;
using ArkPar.Screens;
using Microsoft.UI.Xaml;
using Microsoft.Win32;

namespace ArkPar.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    /// <summary>The Settings screen. Every engine task takes what it may use from it (<see cref="SettingsScreen.Processing"/>).</summary>
    internal static SettingsScreen Settings { get; } = new(
        new SettingsFile(SettingsFile.DefaultPath),
        new Par2Association(Registry.CurrentUser, Environment.ProcessPath ?? throw new InvalidOperationException("The app's own path is unknown.")),
        Environment.ProcessorCount,
        ShellChange.NotifyAssociationsChanged);

    /// <summary>ArkPar's icon as a file next to the app, for the window and the notifications.</summary>
    internal static string IconPath { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "ArkPar.ico");

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // The user may have moved the ArkPar folder since the association was made.
        Settings.KeepPar2FilesOpeningHere();

        _window = new MainWindow();
        _window.Activate();

        // "ArkPar.exe <file>.par2" (a double click on it) verifies that file at once.
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Length > 1 && arguments[1].EndsWith(".par2", StringComparison.OrdinalIgnoreCase))
        {
            _window.Open(Path.GetFullPath(arguments[1]));
        }
    }
}
