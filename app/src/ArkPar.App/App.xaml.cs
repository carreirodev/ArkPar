// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.UI.Xaml;

namespace ArkPar.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
