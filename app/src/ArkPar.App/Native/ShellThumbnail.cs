// SPDX-License-Identifier: GPL-2.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ArkPar.App.Native;

/// <summary>A thumbnail's pixels: 32-bit BGRA, premultiplied, top row first.</summary>
internal sealed record ThumbnailPixels(int Width, int Height, byte[] Bgra);

/// <summary>
/// The thumbnail Windows Explorer shows for a file, through the shell's IShellItemImageFactory.
/// WinRT's StorageFile.GetThumbnailAsync only gives the file type's icon to an unpackaged app,
/// so the app asks the shell directly.
/// </summary>
internal static class ShellThumbnail
{
    // SIIGBF_THUMBNAILONLY: fail instead of falling back to the file type's icon.
    private const int ThumbnailOnly = 0x08;

    private static readonly Guid ImageFactoryId = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    /// <summary>
    /// The thumbnail of <paramref name="path"/>, fit within <paramref name="size"/> pixels. Blocks while
    /// Windows makes it, so call it off the screen's thread.
    /// </summary>
    /// <exception cref="COMException">Windows has no thumbnail for this file (only an icon), or could not make one, e.g. a damaged photo.</exception>
    public static ThumbnailPixels Get(string path, int size)
    {
        var factoryId = ImageFactoryId;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref factoryId, out var factory);
        try
        {
            Marshal.ThrowExceptionForHR(factory.GetImage(new NativeSize(size, size), ThumbnailOnly, out var bitmap));
            try
            {
                return PixelsOf(bitmap);
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    private static ThumbnailPixels PixelsOf(IntPtr bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<NativeBitmap>(), out var info) == 0)
        {
            throw new Win32Exception("GetObject failed on the thumbnail.");
        }

        var header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = info.Width,
            Height = -info.Height, // negative: top row first
            Planes = 1,
            BitCount = 32,
        };
        var pixels = new byte[info.Width * info.Height * 4];
        var screen = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(screen, bitmap, 0, (uint)info.Height, pixels, ref header, DibRgbColors) == 0)
            {
                throw new Win32Exception("GetDIBits failed on the thumbnail.");
            }
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, screen);
        }

        // Photo thumbnails often come with every alpha byte at 0, which would draw as fully transparent.
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length && !hasAlpha; i += 4)
        {
            hasAlpha = pixels[i] != 0;
        }

        if (!hasAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 0xFF;
            }
        }

        return new ThumbnailPixels(info.Width, info.Height, pixels);
    }

    private const uint DibRgbColors = 0;

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeSize(int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetObject(IntPtr bitmap, int size, out NativeBitmap info);

    [DllImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetDIBits(IntPtr deviceContext, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);
}
