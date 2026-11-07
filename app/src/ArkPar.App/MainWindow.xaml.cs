// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.App.Native;
using ArkPar.Screens;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ArkPar.App;

public sealed partial class MainWindow : Window
{
    // Opening size in device-independent pixels, scaled to the screen's DPI below.
    private const int Width = 1200;
    private const int Height = 860;

    private readonly TaskNotices _notices;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);

        // The minimize, maximize and close buttons follow Windows' light or dark mode as it changes, like the rest.
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;

        var scale = WindowDpi.Of(Win32Interop.GetWindowFromWindowId(AppWindow.Id)) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(Width * scale), (int)(Height * scale)));

        Create.Attach(AppWindow.Id);
        Verify.Attach(AppWindow.Id);
        Navigation.SelectedItem = CreateItem;

        // A task that ends while the user looks elsewhere (minimized, another app in front) is told by a notification.
        // TryEnqueue fails only once the window is closing, when there is nothing left to bring back.
        _notices = new TaskNotices(() => DispatcherQueue.TryEnqueue(BringBack), App.Settings.ShowNotificationsUnavailable);
        Create.Screen.Finished += OnTaskFinished;
        Verify.Screen.Finished += OnTaskFinished;
        Verify.FolderVerification.Finished += OnTaskFinished;

        Root.Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            // Both engines are ended, even if removing what the create left half written fails.
            try
            {
                Create.Close();
            }
            finally
            {
                Verify.Close();
            }
        };
    }

    /// <summary>Verifies <paramref name="recoveryFile"/> right away, as when the app is opened with one.</summary>
    public void Open(string recoveryFile)
    {
        Navigation.SelectedItem = VerifyItem;
        Verify.Open(recoveryFile);
    }

    private void OnTaskFinished(object? sender, Banner banner)
    {
        if (!Foreground.Is(Win32Interop.GetWindowFromWindowId(AppWindow.Id)))
        {
            _notices.Show(banner);
        }
    }

    private void BringBack()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
    }

    // Asked once, the first time ArkPar opens; Configurações changes the answer later.
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!App.Settings.MustAskAboutPar2Files)
        {
            return;
        }

        var question = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Abrir os arquivos .par2 com o ArkPar?",
            Content = "Assim, dois cliques num arquivo .par2 no Explorer abrem o ArkPar e verificam o conjunto. Você pode mudar isso depois em Configurações.",
            PrimaryButtonText = "Abrir com o ArkPar",
            CloseButtonText = "Não",
            DefaultButton = ContentDialogButton.Primary,
        };
        App.Settings.AnswerPar2Question(await question.ShowAsync() == ContentDialogResult.Primary);

        // Windows still opens them with another app, or the change failed: Configurações says what to do.
        if (App.Settings.AnotherAppChosen || App.Settings.HasBanner)
        {
            Navigation.SelectedItem = SettingsItem;
        }
    }

    private void OnNavigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        Create.Visibility = ReferenceEquals(args.SelectedItem, CreateItem) ? Visibility.Visible : Visibility.Collapsed;
        Verify.Visibility = ReferenceEquals(args.SelectedItem, VerifyItem) ? Visibility.Visible : Visibility.Collapsed;
        var showSettings = ReferenceEquals(args.SelectedItem, SettingsItem);
        SettingsView.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
        if (showSettings)
        {
            App.Settings.Refresh();
        }
    }
}
