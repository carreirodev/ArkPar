// SPDX-License-Identifier: GPL-2.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using ArkPar.App.Native;
using ArkPar.Screens;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ArkPar.App;

public sealed partial class ProblemCard : UserControl
{
    public static readonly DependencyProperty FileProperty = DependencyProperty.Register(
        nameof(File), typeof(ProblemFile), typeof(ProblemCard), new PropertyMetadata(null, (card, _) => ((ProblemCard)card).Show()));

    // Twice the card's height, so the thumbnail stays sharp on a high-DPI screen.
    private const int ThumbnailSize = 320;

    public ProblemCard()
    {
        InitializeComponent();
    }

    public ProblemFile? File
    {
        get => (ProblemFile?)GetValue(FileProperty);
        set => SetValue(FileProperty, value);
    }

    private void Show()
    {
        var file = File;
        Thumbnail.Source = null;
        ToolTipService.SetToolTip(Placeholder, null);
        if (file is null)
        {
            return;
        }

        VisualStateManager.GoToState(this, file.Kind.ToString(), useTransitions: false);
        TagText.Text = file.Tag;
        NameText.Text = file.Name;
        ToolTipService.SetToolTip(NameText, file.Name);
        MetaText.Text = file.Meta;

        if (file.ImagePath is null)
        {
            PlaceholderText.Text = "sem arquivo";
            return;
        }

        PlaceholderText.Text = "";
        _ = LoadThumbnailAsync(file, file.ImagePath);
    }

    /// <summary>The thumbnail Windows Explorer shows. Files Windows only has an icon for show "sem miniatura".</summary>
    private async Task LoadThumbnailAsync(ProblemFile file, string path)
    {
        ThumbnailPixels pixels;
        try
        {
            pixels = await Task.Run(() => ShellThumbnail.Get(path, ThumbnailSize)).ConfigureAwait(true);
        }
        catch (Exception failure) when (failure is COMException or Win32Exception or FileNotFoundException or UnauthorizedAccessException)
        {
            // No thumbnail: a file type without one, a damaged photo the decoder rejects, a file just removed.
            // The card says so, and the reason is in its tooltip.
            if (File == file)
            {
                PlaceholderText.Text = "sem miniatura";
                ToolTipService.SetToolTip(Placeholder, failure.Message);
            }

            return;
        }

        // The card may have been given another file (a new result) while this one loaded.
        if (File != file)
        {
            return;
        }

        var image = new WriteableBitmap(pixels.Width, pixels.Height);
        using (var buffer = image.PixelBuffer.AsStream())
        {
            await buffer.WriteAsync(pixels.Bgra).ConfigureAwait(true);
        }

        image.Invalidate();
        Thumbnail.Source = image;
    }
}
