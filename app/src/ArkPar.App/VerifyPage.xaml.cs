// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;
using ArkPar.Screens;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace ArkPar.App;

public sealed partial class VerifyPage : UserControl
{
    private WindowId _window;

    public VerifyPage()
    {
        InitializeComponent();
    }

    public VerifyScreen Screen { get; } = new((command, progress) => EngineRun.Start(command, progress));

    /// <summary>The window the file picker opens over.</summary>
    public void Attach(WindowId window) => _window = window;

    public void Open(string recoveryFile) => _ = Screen.VerifyAsync(recoveryFile);

    /// <summary>Ends the engine if it is still running.</summary>
    public void Close() => Screen.Dispose();

    private async void OnChooseFile(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(_window);
        picker.FileTypeFilter.Add(".par2");
        var picked = await picker.PickSingleFileAsync();
        if (picked is not null && !Screen.IsRunning)
        {
            await Screen.VerifyAsync(picked.Path);
        }
    }

    private async void OnVerifyAgain(object sender, RoutedEventArgs e)
    {
        if (Screen.RecoveryFile is { } recoveryFile && !Screen.IsRunning)
        {
            await Screen.VerifyAsync(recoveryFile);
        }
    }

    private async void OnRepair(object sender, RoutedEventArgs e)
    {
        if (Screen.CanRepair)
        {
            await Screen.RepairAsync();
        }
    }

    private void OnTogglePause(object sender, RoutedEventArgs e) => Screen.TogglePause();

    private void OnCancel(object sender, RoutedEventArgs e) => Screen.Cancel();
}
