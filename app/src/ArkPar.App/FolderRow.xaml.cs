// SPDX-License-Identifier: GPL-2.0-or-later

using System.ComponentModel;
using ArkPar.Screens;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ArkPar.App;

public sealed partial class FolderRow : UserControl
{
    public static readonly DependencyProperty FolderProperty = DependencyProperty.Register(
        // WinUI hands the callback the object the property is registered on, so it is always a FolderRow.
        nameof(Folder), typeof(CheckedFolder), typeof(FolderRow), new PropertyMetadata(null, (row, change) => ((FolderRow)row).Follow(change.OldValue, change.NewValue)));

    public FolderRow()
    {
        InitializeComponent();
        Unloaded += (_, _) => Follow(Folder, null);
        Loaded += (_, _) => Follow(null, Folder);
    }

    /// <summary>The user asked for a set's detail.</summary>
    public event EventHandler<CheckedSet>? OpenRequested;

    public CheckedFolder? Folder
    {
        get => (CheckedFolder?)GetValue(FolderProperty);
        set => SetValue(FolderProperty, value);
    }

    private void Follow(object? oldFolder, object? newFolder)
    {
        if (oldFolder is CheckedFolder old)
        {
            old.PropertyChanged -= OnFolderChanged;
        }

        if (newFolder is CheckedFolder folder)
        {
            // Following twice (set, then loaded) would show each change twice: once is enough.
            folder.PropertyChanged -= OnFolderChanged;
            folder.PropertyChanged += OnFolderChanged;
            ListButton.IsChecked = false;
        }

        Show();
    }

    private void OnFolderChanged(object? sender, PropertyChangedEventArgs e) => Show();

    private void Show()
    {
        if (Folder is not { } folder)
        {
            return;
        }

        NameText.Text = folder.Name;
        ToolTipService.SetToolTip(NameText, folder.Path);
        HealthLine.Text = folder.HealthText;
        SetsText.Text = folder.SetsText;
        VisualStateManager.GoToState(this, folder.Group?.ToString() ?? "Pending", useTransitions: false);

        // Nothing to show before the set is verified.
        OpenButton.Visibility = folder.HasSeveralSets || folder.Group is null ? Visibility.Collapsed : Visibility.Visible;
        ListButton.Visibility = folder.HasProblemList ? Visibility.Visible : Visibility.Collapsed;
        if (!folder.HasProblemList)
        {
            ListButton.IsChecked = false;
        }

        ProblemList.ItemsSource = folder.ProblemSets;
        ProblemList.Visibility = ListButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnToggleList(object sender, RoutedEventArgs e) =>
        ProblemList.Visibility = ListButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (Folder is { Sets: [var only] })
        {
            OpenRequested?.Invoke(this, only);
        }
    }

    private void OnOpenSet(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CheckedSet set })
        {
            OpenRequested?.Invoke(this, set);
        }
    }
}
