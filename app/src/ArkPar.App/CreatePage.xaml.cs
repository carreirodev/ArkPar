// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;
using ArkPar.Screens;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace ArkPar.App;

public sealed partial class CreatePage : UserControl
{
    private WindowId _window;

    public CreatePage()
    {
        InitializeComponent();

        // Set here, from the screen's own values: an x:Bind to a static member crashes the XAML compiler (WMC9999).
        BlockSizeBox.ItemsSource = CreateScreen.BlockSizes;
        MemoryBox.ItemsSource = CreateScreen.MemoryLimits;
        BlockSizeBox.SelectedIndex = 0;
        MemoryBox.SelectedIndex = 0;
        // Value before Minimum: raising the minimum first would push the slider's 0 up to it, and the
        // ValueChanged handler would hand that 1% to the screen instead of its 10%.
        var redundancy = Screen.Redundancy;
        RedundancySlider.Maximum = CreateScreen.MaxRedundancy;
        RedundancySlider.Value = redundancy;
        RedundancySlider.Minimum = CreateScreen.MinRedundancy;
        RecoveryFileCountBox.Minimum = 1;
        RecoveryFileCountBox.Maximum = CreateScreen.MaxRecoveryFiles;

        FilesTree.Expanding += (_, args) => AddChildren(args.Node);
        Screen.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CreateScreen.Tree))
            {
                ShowTree();
            }
        };
    }

    /// <summary>
    /// Fills the tree with what the set covers, the folders chosen open. A folder's contents are added only
    /// when it opens, so a folder of thousands of photos does not build thousands of rows up front.
    /// (Built here rather than bound: a bound tree does not reliably open its items.)
    /// </summary>
    private void ShowTree()
    {
        FilesTree.RootNodes.Clear();
        foreach (var node in Screen.Tree)
        {
            var treeNode = NodeOf(node);
            if (node.StartsExpanded)
            {
                AddChildren(treeNode);
                treeNode.IsExpanded = true;
            }

            FilesTree.RootNodes.Add(treeNode);
        }
    }

    private static TreeViewNode NodeOf(SelectionNode node) => new() { Content = node, HasUnrealizedChildren = node.Children.Count > 0 };

    private static void AddChildren(TreeViewNode treeNode)
    {
        if (!treeNode.HasUnrealizedChildren || treeNode.Content is not SelectionNode node)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            treeNode.Children.Add(NodeOf(child));
        }

        treeNode.HasUnrealizedChildren = false;
    }

    public CreateScreen Screen { get; } = new(
        (command, progress) => EngineRun.Start(command, progress),
        (create, stop) => EngineTrial.RunAsync(create, cancellationToken: stop),
        RecycleBin.Send);

    /// <summary>The window the pickers open over.</summary>
    public void Attach(WindowId window) => _window = window;

    /// <summary>Ends the engine if it is still running, removing what it left half written.</summary>
    public void Close() => Screen.Dispose();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (Screen.CanChoose && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Incluir no conjunto";
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!Screen.CanChoose || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();

        // Items without a path (e.g. from a phone in Explorer) cannot be protected where they are.
        await AddAsync(items.Select(item => item.Path).Where(path => !string.IsNullOrEmpty(path)));
    }

    private async void OnChooseFiles(object sender, RoutedEventArgs e)
    {
        var picked = await new FileOpenPicker(_window).PickMultipleFilesAsync();
        await AddAsync(picked.Select(file => file.Path));
    }

    private async void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var picked = await new FolderPicker(_window).PickSingleFolderAsync();
        if (picked is not null)
        {
            await AddAsync([picked.Path]);
        }
    }

    // AddAsync shows every failure on the screen itself.
    private Task AddAsync(IEnumerable<string> paths)
    {
        string[] chosen = [.. paths];
        return chosen.Length > 0 && Screen.CanChoose ? Screen.AddAsync(chosen) : Task.CompletedTask;
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (Screen.CanChoose)
        {
            Screen.Clear();
        }
    }

    private void OnRedundancyChanged(object sender, RangeBaseValueChangedEventArgs e) => Screen.Redundancy = (int)Math.Round(e.NewValue);

    private void OnBlockSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BlockSizeBox.SelectedItem is Choice choice)
        {
            Screen.BlockSize = choice;
        }
    }

    private void OnMemoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MemoryBox.SelectedItem is Choice choice)
        {
            Screen.Memory = choice;
        }
    }

    // An empty box (NaN) is Automático.
    private void OnRecoveryFileCountChanged(NumberBox sender, NumberBoxValueChangedEventArgs e) =>
        Screen.RecoveryFileCount = double.IsNaN(e.NewValue) ? null : (int)Math.Round(e.NewValue);

    // CreateAsync shows every failure on the screen itself.
    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (Screen.CanCreate)
        {
            // The progress and the result show at the top, while the button may be far down a long tree.
            Scroller.ChangeView(horizontalOffset: null, verticalOffset: 0, zoomFactor: null);
            await Screen.CreateAsync();
        }
    }

    private void OnTogglePause(object sender, RoutedEventArgs e) => Screen.TogglePause();

    private void OnCancel(object sender, RoutedEventArgs e) => Screen.Cancel();
}
