# SPDX-License-Identifier: GPL-2.0-or-later
#
# Builds the app's icon, app/src/ArkPar.App/Assets/ArkPar.ico, from its two
# drawings in the same folder: ArkPar-large.svg, the WPC lamp, only at 256 px
# (Explorer's large views, Start), and ArkPar-small.svg at every other size. The
# lamp fades out below that: the taskbar shrinks the window's 56 px icon to 42 px
# at 175% display scale. Run it after changing either drawing, and commit the
# .ico: the build only embeds it, it does not draw it.
#
# Each size is drawn by Chrome (headless) straight at that size, then stored as
# a PNG entry of the .ico, as Windows has read them since Vista.
#
# Usage: pwsh -File app/build-icon.ps1 [-Chrome <path to chrome.exe>]

[CmdletBinding()]
param(
    [string]$Chrome = (Join-Path $env:ProgramFiles 'Google/Chrome/Application/chrome.exe')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Chrome)) {
    throw "Chrome not found at '$Chrome'. Pass its path with -Chrome."
}

$assets = Join-Path $PSScriptRoot 'src/ArkPar.App/Assets'
$icon = Join-Path $assets 'ArkPar.ico'

# Explorer, taskbar and notification sizes at 100% to 200% display scale, and
# Explorer's large views. App/AppIconTests.cs checks the .ico holds these.
$sizes = @(16, 20, 24, 32, 40, 48, 64, 256)

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("ArkPar-icon-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $images = foreach ($size in $sizes) {
        $drawing = Join-Path $assets ($size -lt 256 ? 'ArkPar-small.svg' : 'ArkPar-large.svg')
        $png = Join-Path $work "$size.png"

        # Headless Chrome lays the page out wider than a small window, but its screenshot keeps only the window's
        # top-left corner: the drawing sits there at exactly the icon size. A transparent background keeps the
        # rounded corners see-through.
        $page = Join-Path $work "$size.html"
        Set-Content -Path $page -Encoding utf8NoBOM -Value (
            '<!doctype html><html><body style="margin:0">' +
            "<img src=`"$(([System.Uri]$drawing).AbsoluteUri)`" width=`"$size`" height=`"$size`" style=`"display:block`">" +
            '</body></html>')
        & $Chrome --headless=new --disable-gpu --hide-scrollbars --no-first-run `
            "--user-data-dir=$(Join-Path $work 'chrome')" `
            --default-background-color=00000000 `
            "--window-size=$size,$size" `
            "--screenshot=$png" `
            ([System.Uri]$page).AbsoluteUri 2>&1 | Out-Null
        if (-not (Test-Path $png)) {
            throw "Chrome did not draw '$drawing' at $size px."
        }

        $bytes = [System.IO.File]::ReadAllBytes($png)
        # A PNG's width is the big-endian number at byte 16.
        $width = ([int]$bytes[16] -shl 24) -bor ([int]$bytes[17] -shl 16) -bor ([int]$bytes[18] -shl 8) -bor $bytes[19]
        if ($width -ne $size) {
            throw "Chrome drew '$drawing' $width px wide instead of $size px."
        }

        @{ Size = $size; Bytes = $bytes }
    }

    # ICO: a 6-byte header, one 16-byte entry per image, then the images.
    $stream = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($stream)
    $writer.Write([uint16]0)              # reserved
    $writer.Write([uint16]1)              # 1 = icon
    $writer.Write([uint16]$images.Count)

    $offset = 6 + 16 * $images.Count
    foreach ($image in $images) {
        $side = $image.Size -eq 256 ? 0 : $image.Size   # 0 stands for 256
        $writer.Write([byte]$side)            # width
        $writer.Write([byte]$side)            # height
        $writer.Write([byte]0)                # no palette
        $writer.Write([byte]0)                # reserved
        $writer.Write([uint16]1)              # colour planes
        $writer.Write([uint16]32)             # bits per pixel
        $writer.Write([uint32]$image.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Bytes.Length
    }
    foreach ($image in $images) {
        $writer.Write($image.Bytes)
    }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes($icon, $stream.ToArray())

    Write-Host ("Icon built: {0} ({1:N0} bytes, {2} sizes)" -f $icon, $stream.Length, $images.Count)
}
finally {
    Remove-Item $work -Recurse -Force
}
