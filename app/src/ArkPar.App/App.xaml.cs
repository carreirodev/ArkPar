// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.UI.Xaml;

namespace ArkPar.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        // "ArkPar.exe <file>.par2" verifies that file at once.
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Length > 1 && arguments[1].EndsWith(".par2", StringComparison.OrdinalIgnoreCase))
        {
            _window.Open(Path.GetFullPath(arguments[1]));
        }
    }
}
