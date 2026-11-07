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
        FolderVerification.Follow(Screen);
    }

    // Each task takes the GPU and cores set in Configurações when it starts.
    public VerifyScreen Screen { get; } = new((command, progress) => EngineRun.Start(command, progress, processing: App.Settings.Processing));

    /// <summary>The folder verification; each set's detail is <see cref="Screen"/>.</summary>
    public FolderVerifyScreen FolderVerification { get; } = new((command, progress) => EngineRun.Start(command, progress, processing: App.Settings.Processing));

    /// <summary>The window the pickers open over.</summary>
    public void Attach(WindowId window) => _window = window;

    /// <summary>Verifies <paramref name="recoveryFile"/> on its own, leaving a folder verification that is not running.</summary>
    public void Open(string recoveryFile)
    {
        if (Screen.IsRunning || FolderVerification.IsRunning)
        {
            return;
        }

        FolderVerification.Leave();

        // VerifyAsync shows every failure on the screen itself, so nothing is lost by not awaiting it.
        _ = Screen.VerifyAsync(recoveryFile);
    }

    /// <summary>Ends the engines if they are still running.</summary>
    public void Close()
    {
        try
        {
            FolderVerification.Dispose();
        }
        finally
        {
            Screen.Dispose();
        }
    }

    private async void OnChooseFile(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(_window);
        picker.FileTypeFilter.Add(".par2");
        var picked = await picker.PickSingleFileAsync();
        if (picked is not null && !Screen.IsRunning && !FolderVerification.IsRunning)
        {
            FolderVerification.Leave();
            await Screen.VerifyAsync(picked.Path);
        }
    }

    private async void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var picked = await new FolderPicker(_window).PickSingleFolderAsync();
        if (picked is not null && !Screen.IsRunning && !FolderVerification.IsRunning)
        {
            // ChooseAsync shows every failure on the screen itself.
            await FolderVerification.ChooseAsync(picked.Path);
        }
    }

    private async void OnStartFolder(object sender, RoutedEventArgs e)
    {
        if (FolderVerification.CanStart)
        {
            await FolderVerification.StartAsync();
        }
    }

    private async void OnRepairAll(object sender, RoutedEventArgs e)
    {
        if (FolderVerification.CanRepairAll)
        {
            await FolderVerification.RepairAllAsync();
        }
    }

    private async void OnOpenSet(object? sender, CheckedSet set)
    {
        if (FolderVerification.CanOpen)
        {
            await FolderVerification.OpenAsync(set);
        }
    }

    private void OnBackToSummary(object sender, RoutedEventArgs e)
    {
        if (!Screen.IsRunning)
        {
            FolderVerification.CloseDetail();
        }
    }

    private async void OnVerifyAgain(object sender, RoutedEventArgs e)
    {
        if (Screen.RecoveryFile is { } recoveryFile && !Screen.IsRunning && !FolderVerification.IsRunning)
        {
            await Screen.VerifyAsync(recoveryFile);
        }
    }

    private async void OnRepair(object sender, RoutedEventArgs e)
    {
        if (Screen.CanRepair && !FolderVerification.IsRunning)
        {
            await Screen.RepairAsync();
        }
    }

    private void OnTogglePause(object sender, RoutedEventArgs e) => Screen.TogglePause();

    private void OnCancel(object sender, RoutedEventArgs e) => Screen.Cancel();

    private void OnFolderTogglePause(object sender, RoutedEventArgs e) => FolderVerification.TogglePause();

    private void OnFolderCancel(object sender, RoutedEventArgs e) => FolderVerification.Cancel();
}
