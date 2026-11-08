# SPDX-License-Identifier: GPL-2.0-or-later
#
# Builds the package that takes ArkPar to any 64-bit Windows 11 PC: a zip with
# one folder holding the app, the engine, the LEIA-ME and the license texts.
# Nothing has to be installed first: the publish is self-contained for both
# .NET and the Windows App SDK (ADR-0002), so the user unzips and opens
# ArkPar.exe.
#
# The publish settings live here, not in the project file, so `dotnet build`
# and `dotnet run` keep the quick framework-dependent build:
#   - SelfContained + WindowsAppSDKSelfContained: carry both runtimes.
#   - EnableMsixTooling: without it, publishing an unpackaged WinUI app leaves
#     ArkPar.pri out of the folder, and the app closes as soon as it opens.
#
# Usage: pwsh -File app/build-package.ps1 [-PlatformToolset v145]
# Output: app/artifacts/ArkPar-win-x64.zip (unzips to an ArkPar folder)

[CmdletBinding()]
param(
    # Passed on to build-engine.ps1.
    [string]$PlatformToolset = 'v145'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $PSScriptRoot 'src/ArkPar.App'
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$staging = Join-Path $artifacts 'package'
$packageDir = Join-Path $staging 'ArkPar'
$licensesDir = Join-Path $packageDir 'Licenças'
$zip = Join-Path $artifacts 'ArkPar-win-x64.zip'

# 1. The engine, built fresh from source/par2j.
& (Join-Path $PSScriptRoot 'build-engine.ps1') -PlatformToolset $PlatformToolset

# 2. The app, self-contained, into a clean folder.
if (Test-Path $staging) {
    Remove-Item $staging -Recurse -Force
}

dotnet publish $appProject --nologo --verbosity quiet `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:WindowsAppSDKSelfContained=true `
    -p:EnableMsixTooling=true `
    --output $packageDir
if ($LASTEXITCODE -ne 0) {
    throw "App publish failed (dotnet exit code $LASTEXITCODE)."
}

# The engine is copied only if it existed when the project was evaluated, and a
# missing runtime file shows up only on a PC that lacks it: check them here.
$required = @(
    'ArkPar.exe'                # the app
    'ArkPar.pri'                # the screens' resources (see EnableMsixTooling above)
    'engine/par2j64.exe'        # the engine
    'coreclr.dll'               # .NET, carried inside
    'Microsoft.UI.Xaml.dll'     # Windows App SDK, carried inside
)
foreach ($file in $required) {
    if (-not (Test-Path (Join-Path $packageDir $file))) {
        throw "The package is missing '$file'."
    }
}

# The Windows App SDK's AI and machine-learning parts, unused by the app, came
# in with the full Microsoft.WindowsAppSDK package and made the zip 25 MB bigger
# (WPC-403). Their two biggest files mark that they are back.
$unwanted = @(
    'onnxruntime.dll'
    'DirectML.dll'
)
foreach ($file in $unwanted) {
    if (Test-Path (Join-Path $packageDir $file)) {
        throw "The package carries '$file': reference only the Windows App SDK parts the app uses (see ArkPar.App.csproj)."
    }
}

# 3. The texts that travel with it.
New-Item -ItemType Directory -Path $licensesDir | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'package/LEIA-ME.txt') $packageDir

# The engine's license and its readme (credits and third-party notices), unchanged.
$engineSource = Join-Path $repoRoot 'source/par2j'
Copy-Item (Join-Path $engineSource 'License_GPL.txt') (Join-Path $licensesDir 'ArkPar e motor - GPL-2.0.txt')
Copy-Item (Join-Path $engineSource 'ReadMe2_English.txt') (Join-Path $licensesDir 'Motor (par2j, MultiPar) - ReadMe2_English.txt')

# The Microsoft runtimes carried inside: their license terms and notices, taken
# from the exact packages this publish used.
$assets = Get-Content (Join-Path $appProject 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable

# NuGet may list fallback folders next to the main one: use whichever holds the package.
function Find-PackageDir([string]$relativePath) {
    foreach ($root in $assets.packageFolders.Keys) {
        $dir = Join-Path $root $relativePath
        if (Test-Path $dir) {
            return $dir
        }
    }
    throw "Package folder '$relativePath' not found in any NuGet package folder."
}

$runtimeNotices = [System.Collections.Generic.List[object]]::new()
# Sorted by name, so a text shared by several packages is always named after
# the same one (Microsoft.WindowsAppSDK.Base, which every other part needs).
$windowsAppSdk = @($assets.libraries.GetEnumerator() |
    Where-Object Key -like 'Microsoft.WindowsAppSDK*' |
    ForEach-Object { @{ Name = ($_.Key -split '/')[0]; Dir = Find-PackageDir $_.Value.path } } |
    Sort-Object { $_.Name })
if ($windowsAppSdk.Count -eq 0) {
    throw 'The Windows App SDK packages are not listed in project.assets.json.'
}
foreach ($library in $windowsAppSdk) {
    $runtimeNotices.Add($library)
}

$framework = @($assets.project.frameworks.Values)[0]
$netRuntime = $framework.downloadDependencies | Where-Object name -eq 'Microsoft.NETCore.App.Runtime.win-x64'
if (-not $netRuntime) {
    throw 'The .NET runtime pack is not listed in project.assets.json.'
}
$netVersion = $netRuntime.version.Trim('[]').Split(',')[0].Trim()
$runtimeNotices.Add(@{ Name = '.NET'; Dir = Find-PackageDir "microsoft.netcore.app.runtime.win-x64/$netVersion" })

# Several Windows App SDK packages carry the same text: keep one copy of each.
$copied = @{}
foreach ($runtime in $runtimeNotices) {
    $texts = Get-ChildItem $runtime.Dir -File | Where-Object Name -match '^(license|notice|third-party-notices)\.txt$'
    if (-not $texts) {
        throw "No license text found in '$($runtime.Dir)'."
    }
    foreach ($text in $texts) {
        $hash = (Get-FileHash $text.FullName -Algorithm SHA256).Hash
        if (-not $copied.ContainsKey($hash)) {
            $copied[$hash] = $true
            Copy-Item $text.FullName (Join-Path $licensesDir "$($runtime.Name) - $($text.Name)")
        }
    }
}

# 4. The zip, with the ArkPar folder at its top.
if (Test-Path $zip) {
    Remove-Item $zip -Force
}
Compress-Archive -Path $packageDir -DestinationPath $zip -CompressionLevel Optimal

Write-Host ("Package built: {0} ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
