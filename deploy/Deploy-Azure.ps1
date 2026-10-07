<#
.SYNOPSIS
    Deploys the API to Azure App Service on Windows (IIS + ASP.NET Core Module, in-process) with
    KingsRow and the Chinook databases, then waits for /healthz.

.DESCRIPTION
    Uses the az CLI's current login and subscription. Layout on the App Service:
      D:\home\site\wwwroot            the API, and engine-host\ (the worker, self-contained x64)
      D:\home\data\kingsrow           KingsRow: egdb64.dll, engines\Kingsrow64.dll and its data files
      D:\home\data\chinook            the Chinook databases
      D:\home\LogFiles\checkers-api   the request log, and KingsRow's own log in kingsrow\
    The engine and databases live outside wwwroot, so a code deploy never re-uploads them; they are
    uploaded unless -SkipData is given.

    App Service's sandbox forbids registry writes (KingsRow carries on without saving its settings;
    the worker sets them all at start anyway) and gives the app no Documents folder (the worker
    redirects KingsRow's log). The worker is self-contained because the Free tier runs a 32-bit w3wp
    while KingsRow needs a 64-bit process.

    F1 (Free): 1 GB memory and 1 GB disk shared by everything, 60 CPU minutes a day, and the app is
    unloaded after 20 minutes idle, so the first request after that takes a few seconds while the
    workers start. The engine caches are reduced to fit. B1 has no idle unload and runs 64-bit.

.EXAMPLE
    .\deploy\Deploy-Azure.ps1 -AppName checkers-chinook-api -Location polandcentral -Sku F1
#>
param(
    [Parameter(Mandatory)] [string] $AppName,
    [string] $ResourceGroup = 'checkers-chinook',
    [string] $Location = 'polandcentral',
    [ValidateSet('F1', 'B1')] [string] $Sku = 'F1',
    [string] $Runtime = 'dotnet:10',
    [string] $EngineDir = 'C:\engines\kingsrow',
    [string] $DatabaseDir = 'D:\tb\chinook',
    [switch] $SkipData
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Invoke-Az {
    az @args
    if ($LASTEXITCODE -ne 0) { throw "az $($args -join ' ') failed with exit code $LASTEXITCODE." }
}

function Deploy-Zip([string] $zip, [string] $targetPath, [bool] $clean) {
    Write-Host "Uploading $(Split-Path $zip -Leaf) ($([Math]::Round((Get-Item $zip).Length / 1MB)) MB) to $targetPath ..."
    Invoke-Az webapp deploy -g $ResourceGroup -n $AppName --src-path $zip --type zip --target-path $targetPath `
        --clean $(if ($clean) { 'true' } else { 'false' }) --restart false --only-show-errors -o none
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$work = Join-Path $repo 'artifacts\azure'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory $work | Out-Null

# --- resources ---------------------------------------------------------------------------------
Write-Host "Resource group $ResourceGroup, plan $AppName-plan ($Sku), app $AppName in $Location"
Invoke-Az group create -n $ResourceGroup -l $Location --only-show-errors -o none
Invoke-Az appservice plan create -g $ResourceGroup -n "$AppName-plan" --sku $Sku -l $Location --only-show-errors -o none
$existing = az webapp list -g $ResourceGroup --query "[?name=='$AppName'].name" -o tsv
if (-not $existing) {
    Invoke-Az webapp create -g $ResourceGroup -p "$AppName-plan" -n $AppName --runtime $Runtime --https-only true --only-show-errors -o none
}
Invoke-Az webapp config set -g $ResourceGroup -n $AppName --use-32bit-worker-process $(if ($Sku -eq 'F1') { 'true' } else { 'false' }) --only-show-errors -o none

$settings = @(
    'Engine__Path=D:\home\data\kingsrow\engines\Kingsrow64.dll',
    'Engine__Databases=D:\home\data\chinook',
    'Engine__HostPath=D:\home\site\wwwroot\engine-host\Checkers.EngineHost.exe',
    'Engine__HashMb=32',
    'Engine__DbCacheMb=64',
    'LogFiles__Directory=D:\home\LogFiles\checkers-api'
)
Invoke-Az webapp config appsettings set -g $ResourceGroup -n $AppName --settings @settings --only-show-errors -o none

# --- engine and databases (outside wwwroot; once) ------------------------------------------------
if (-not $SkipData) {
    $engineStage = Join-Path $work 'kingsrow'
    New-Item -ItemType Directory (Join-Path $engineStage 'engines') | Out-Null
    Copy-Item (Join-Path $EngineDir 'egdb64.dll') $engineStage
    foreach ($file in 'Kingsrow64.dll', 'Kingsrow.odb', 'weights_v4.bin', 'weights_nk_v4.bin') {
        Copy-Item (Join-Path $EngineDir "engines\$file") (Join-Path $engineStage 'engines')
    }
    Compress-Archive (Join-Path $engineStage '*') (Join-Path $work 'kingsrow.zip')
    Deploy-Zip (Join-Path $work 'kingsrow.zip') '/home/data/kingsrow' $true

    # One upload per database file pair keeps each request small; nothing is cleaned in between.
    $databases = Get-ChildItem $DatabaseDir -File | Where-Object { $_.Name -match '^DB\d' } | Group-Object { $_.Name -replace '\.idx$', '' }
    foreach ($group in $databases) {
        $zip = Join-Path $work "$($group.Name).zip"
        Compress-Archive $group.Group.FullName $zip -CompressionLevel Optimal
        Deploy-Zip $zip '/home/data/chinook' $false
    }
}

# --- application --------------------------------------------------------------------------------
$site = Join-Path $work 'site'
dotnet publish (Join-Path $repo 'src\Checkers.Api') -c Release -o $site --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (API) failed.' }
dotnet publish (Join-Path $repo 'src\Checkers.EngineHost') -c Release -r win-x64 --self-contained -o (Join-Path $site 'engine-host') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (worker) failed.' }
Compress-Archive (Join-Path $site '*') (Join-Path $work 'site.zip')
Deploy-Zip (Join-Path $work 'site.zip') '/home/site/wwwroot' $true
Invoke-Az webapp restart -g $ResourceGroup -n $AppName -o none

# --- check --------------------------------------------------------------------------------------
$url = "https://$AppName.azurewebsites.net"
for ($attempt = 1; $attempt -le 40; $attempt++) {
    try {
        $health = Invoke-RestMethod "$url/healthz" -TimeoutSec 20
        if ($health.ok) {
            Write-Host "Healthy: $($health.workers)/$($health.configuredWorkers) workers, $($health.engineName)" -ForegroundColor Green
            Write-Host "Test board: $url/"
            exit 0
        }
    } catch {
    }

    Start-Sleep -Seconds 5
}

throw "$url did not become healthy; see D:\home\LogFiles\checkers-api (Kudu: https://$AppName.scm.azurewebsites.net)."
