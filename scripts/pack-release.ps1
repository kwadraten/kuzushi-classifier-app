[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$Runtime = 'win-x64',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $root ".tmp/release/$Version"
$pluginProject = Join-Path $root 'KuzushiClassifierApp.Plugin/KuzushiClassifierApp.Plugin.csproj'
$desktopProject = Join-Path $root 'KuzushiClassifierApp.Desktop/KuzushiClassifierApp.Desktop.csproj'
$noRestoreArgs = $NoRestore ? @('--no-restore') : @()

dotnet test (Join-Path $root 'tests/KuzushiClassifierApp.Plugin.Tests/KuzushiClassifierApp.Plugin.Tests.csproj') -c Release @noRestoreArgs
if ($LASTEXITCODE -ne 0) { throw 'Plugin tests failed.' }

dotnet publish $pluginProject -c Release -r $Runtime --self-contained true @noRestoreArgs
if ($LASTEXITCODE -ne 0) { throw 'Plugin publish failed.' }
dotnet publish $desktopProject -c Release -r $Runtime --self-contained true @noRestoreArgs
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$pluginPublish = Join-Path $root "KuzushiClassifierApp.Plugin/bin/Release/net10.0/$Runtime/publish"
# Debug symbols are not part of the shipped plugin: libSkiaSharp.pdb alone adds ~80 MB.
Remove-Item -Path (Join-Path $pluginPublish '*.pdb') -Force -ErrorAction SilentlyContinue
$pluginZip = Join-Path $releaseRoot "kwadraten.kuzushi-classifier-$Version-$Runtime.zip"
if (Test-Path -LiteralPath $pluginZip) { Remove-Item -LiteralPath $pluginZip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($pluginPublish, $pluginZip)

$inno = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if (-not $inno) {
    $candidate = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
    if (Test-Path -LiteralPath $candidate) { $inno = Get-Command $candidate }
}
if (-not $inno) { throw 'Inno Setup 6 (ISCC.exe) is required to build the desktop installer.' }

& $inno.Source "/O$releaseRoot" (Join-Path $root 'pack-win-x64.iss')
if ($LASTEXITCODE -ne 0) { throw 'Desktop installer build failed.' }

Get-ChildItem -LiteralPath $releaseRoot -File | Where-Object Name -ne 'hashes.json' | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    [pscustomobject]@{ Name = $_.Name; Length = $_.Length; SHA256 = $hash }
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $releaseRoot 'hashes.json') -Encoding utf8
Get-ChildItem -LiteralPath $releaseRoot -File | Select-Object Name, Length, FullName
