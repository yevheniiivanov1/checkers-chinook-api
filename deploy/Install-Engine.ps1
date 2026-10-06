<#
.SYNOPSIS
    Downloads and installs KingsRow (64-bit) and the Chinook endgame databases.

.DESCRIPTION
    KingsRow (Ed Gilbert, https://edgilbert.org/EnglishCheckers/KingsRowEnglish.htm) is a free
    checkers engine for Windows that implements the CheckerBoard engine API and reads the Chinook
    WLD databases. Chinook itself is not distributed as a program; its 2-8 piece endgame databases
    are (Jonathan Schaeffer, https://webdocs.cs.ualberta.ca/~chinook/databases/).

    Download sizes: KingsRow installer 28 MB; databases up to 6 pieces 28 MB, up to 7 pieces
    ~230 MB, up to 8 pieces ~2.7 GB (5.6 GB unpacked).

    The KingsRow installer runs silently into -EngineDir. The engine DLL ends up in
    <EngineDir>\engines\Kingsrow64.dll, which is what Engine:Path should point to.

.EXAMPLE
    .\deploy\Install-Engine.ps1 -EngineDir C:\engines\kingsrow -DatabaseDir D:\tb\chinook -MaxPieces 8
#>
param(
    [string] $EngineDir = 'C:\engines\kingsrow',
    [string] $DatabaseDir = 'D:\tb\chinook',
    [ValidateSet(6, 7, 8)] [int] $MaxPieces = 8,
    [string] $KingsRowInstaller = 'KingsrowSetup64.1.20.exe',
    # SHA-256 of the installer above as downloaded from edgilbert.org on 2026-10-06. The script runs
    # no installer whose hash differs; a newer version needs its own hash passed here.
    [string] $KingsRowSha256 = '889af593e3a01b9fec99047f1e091d593834fde712065c93b75ec139c4b10c65'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # the progress bar slows Invoke-WebRequest down dramatically
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$downloads = Join-Path $env:TEMP 'checkers-engine-downloads'
New-Item -ItemType Directory -Force $downloads, $DatabaseDir | Out-Null

# --- KingsRow --------------------------------------------------------------------------------
$engineDll = Join-Path $EngineDir 'engines\Kingsrow64.dll'
if (Test-Path $engineDll) {
    Write-Host "KingsRow already installed: $engineDll"
} else {
    $installer = Join-Path $downloads $KingsRowInstaller
    Write-Host "Downloading $KingsRowInstaller ..."
    Invoke-WebRequest "https://edgilbert.org/EnglishCheckers/downloads/$KingsRowInstaller" -OutFile $installer -UseBasicParsing
    $hash = (Get-FileHash $installer -Algorithm SHA256).Hash
    if ($hash -ne $KingsRowSha256) {
        Remove-Item $installer
        throw "$KingsRowInstaller has SHA-256 $hash, expected $KingsRowSha256. Not running it."
    }
    Write-Host "Installing KingsRow into $EngineDir ..."
    $setup = Start-Process $installer -Wait -PassThru -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/NOICONS', "/DIR=`"$EngineDir`"")
    if ($setup.ExitCode -ne 0 -or -not (Test-Path $engineDll)) { throw "KingsRow setup failed (exit code $($setup.ExitCode))." }
}

# --- Chinook databases -----------------------------------------------------------------------
$files = @('DB6')
if ($MaxPieces -ge 7) { $files += 0..4 | ForEach-Object { "DB7.$_" } }
if ($MaxPieces -ge 8) { $files += '00', '10', '20', '30', '40', '11', '21', '31', '41', '22', '32', '42', '33', '43', '44' | ForEach-Object { "DB8.$_" } }

foreach ($name in $files) {
    if ((Test-Path (Join-Path $DatabaseDir $name)) -and (Test-Path (Join-Path $DatabaseDir "$name.idx"))) {
        Write-Host "$name already present"
        continue
    }

    $zip = Join-Path $downloads "$name.zip"
    Write-Host "Downloading $name.zip ..."
    Invoke-WebRequest "https://webdocs.cs.ualberta.ca/~chinook/DataBases/$name.zip" -OutFile $zip -UseBasicParsing
    Expand-Archive $zip -DestinationPath $DatabaseDir -Force
    Remove-Item $zip
}

Write-Host ''
Write-Host "Engine:Path      = $engineDll"
Write-Host "Engine:Databases = $DatabaseDir  (Chinook 2-$MaxPieces pieces)"
