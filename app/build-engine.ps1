# SPDX-License-Identifier: GPL-2.0-or-later
#
# Builds the engine (par2j64.exe) from the upstream sources in source/par2j
# without editing them (ADR-0001). Everything that differs from the upstream
# project file is passed on the MSBuild command line:
#   - PlatformToolset: upstream asks for v143 (VS 2022); we build with the
#     toolset of the installed Build Tools (v145 = VS 2026).
#   - TargetName: upstream builds par2j.exe for both platforms and renames the
#     x64 one by hand; we name it par2j64.exe directly.
#   - OutDir/IntDir: keep all build output under app/artifacts (gitignored),
#     never inside source/.
#
# Usage: pwsh -File app/build-engine.ps1 [-PlatformToolset v145]
# Output: app/artifacts/engine/par2j64.exe

[CmdletBinding()]
param(
    [string]$PlatformToolset = 'v145'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'source/par2j/par2j.vcxproj'
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$outDir = Join-Path $artifacts 'engine'
$intDir = Join-Path $artifacts 'obj/engine'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe not found at '$vswhere'. Install Visual Studio Build Tools 2026 with the C++ workload."
}

$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
    -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) {
    throw 'MSBuild not found. Install Visual Studio Build Tools 2026 with the C++ workload.'
}

# MSBuild needs a trailing separator on OutDir/IntDir.
& $msbuild $project /nologo /verbosity:minimal /restore:false `
    /p:Configuration=Release `
    /p:Platform=x64 `
    "/p:PlatformToolset=$PlatformToolset" `
    /p:TargetName=par2j64 `
    "/p:OutDir=$outDir\" `
    "/p:IntDir=$intDir\"
if ($LASTEXITCODE -ne 0) {
    throw "Engine build failed (MSBuild exit code $LASTEXITCODE)."
}

$exe = Join-Path $outDir 'par2j64.exe'
if (-not (Test-Path $exe)) {
    throw "Build reported success but '$exe' does not exist."
}
Write-Host "Engine built: $exe"
