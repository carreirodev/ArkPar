// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace ArkPar.Tests.App;

/// <summary>
/// ArkPar.ico, the icon Explorer, the taskbar and the notifications show. Windows picks the entry closest to the size
/// it draws, so each size it asks for has an entry of its own, drawn for that size (app/build-icon.ps1).
/// </summary>
[TestClass]
public sealed class AppIconTests
{
    // Explorer, taskbar and notification sizes at 100% to 200% display scale, and Explorer's large views.
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 256];

    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    [TestMethod]
    public void HoldsOneImageForEachSizeWindowsDraws()
    {
        var icon = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "ArkPar.ico"));

        Assert.AreEqual(0, BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(0)), "reserved");
        Assert.AreEqual(1, BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(2)), "type 1 is an icon");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(4));

        var sizes = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var entry = icon.AsSpan(6 + (16 * i), 16);

            // A width or height of 0 stands for 256.
            var width = entry[0] == 0 ? 256 : entry[0];
            var height = entry[1] == 0 ? 256 : entry[1];
            Assert.AreEqual(width, height, $"entry {i} is square");

            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            Assert.IsLessThanOrEqualTo(icon.Length, offset + length, $"entry {i} lies inside the file");

            // Each image is a PNG with an alpha channel (colour type 6), so the rounded corners stay see-through.
            var image = icon.AsSpan(offset, length);
            CollectionAssert.AreEqual(PngSignature, image[..8].ToArray(), $"entry {i} is a PNG");
            Assert.AreEqual(width, BinaryPrimitives.ReadInt32BigEndian(image[16..]), $"entry {i}'s PNG is {width} px wide");
            Assert.AreEqual(height, BinaryPrimitives.ReadInt32BigEndian(image[20..]), $"entry {i}'s PNG is {height} px high");
            Assert.AreEqual(6, image[25], $"entry {i}'s PNG has an alpha channel");

            sizes.Add(width);
        }

        CollectionAssert.AreEqual(Sizes, sizes);
    }
}
