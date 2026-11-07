// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.App.Native;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ArkPar.App;

public sealed partial class MainWindow : Window
{
    // Opening size in device-independent pixels, scaled to the screen's DPI below.
    private const int Width = 1200;
    private const int Height = 860;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);

        var scale = WindowDpi.Of(Win32Interop.GetWindowFromWindowId(AppWindow.Id)) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(Width * scale), (int)(Height * scale)));

        Create.Attach(AppWindow.Id);
        Verify.Attach(AppWindow.Id);
        Navigation.SelectedItem = CreateItem;
        Closed += (_, _) =>
        {
            Create.Close();
            Verify.Close();
        };
    }

    /// <summary>Verifies <paramref name="recoveryFile"/> right away, as when the app is opened with one.</summary>
    public void Open(string recoveryFile)
    {
        Navigation.SelectedItem = VerifyItem;
        Verify.Open(recoveryFile);
    }

    private void OnNavigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var showCreate = ReferenceEquals(args.SelectedItem, CreateItem);
        Create.Visibility = showCreate ? Visibility.Visible : Visibility.Collapsed;
        Verify.Visibility = showCreate ? Visibility.Collapsed : Visibility.Visible;
    }
}
