// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;

namespace ArkPar.App;

public sealed partial class MainWindow : Window
{
    // Opening size in device-independent pixels, scaled to the screen's DPI below.
    private const int Width = 1100;
    private const int Height = 820;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);

        var scale = GetDpiForWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id)) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(Width * scale), (int)(Height * scale)));

        Verify.Attach(AppWindow.Id);
        Closed += (_, _) => Verify.Close();
    }

    /// <summary>Verifies <paramref name="recoveryFile"/> right away, as when the app is opened with one.</summary>
    public void Open(string recoveryFile) => Verify.Open(recoveryFile);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetDpiForWindow(IntPtr window);
}
