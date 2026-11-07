// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Screens;
using Microsoft.UI.Xaml.Controls;

namespace ArkPar.App;

public sealed partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    public SettingsScreen Screen { get; } = App.Settings;

    private void OnThreadsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThreadsBox.SelectedItem is Choice choice)
        {
            Screen.Threads = choice;
        }
    }
}
