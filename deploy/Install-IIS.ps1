#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Publishes the API and hosts it in IIS on Windows Server as a normal ASP.NET Core app
    (in-process, ASP.NET Core Module V2; no Windows Service).

.DESCRIPTION
    Prerequisites (once per server):
      Install-WindowsFeature Web-Server, Web-Scripting-Tools, Web-AppInit
      .NET 10 Hosting Bundle (dotnet-hosting-10.x-win.exe), then iisreset
      KingsRow + Chinook databases: .\deploy\Install-Engine.ps1

    What it does:
      - dotnet publish (Release) and copy to -PhysicalPath, keeping logs\
      - writes appsettings.Production.json with the engine and database paths
      - app pool "No Managed Code", AlwaysRunning, no idle timeout, no periodic recycle,
        no overlapped recycle (two app instances would briefly run 2x the engine workers),
        load user profile (KingsRow keeps its settings in HKCU)
      - site with preload enabled, so workers start with the pool rather than on the first request
      - read/execute for the pool identity on the engine and database folders, modify on logs\
      - starts the site and waits for /healthz

.EXAMPLE
    .\deploy\Install-IIS.ps1 -Port 8080 -EnginePath C:\engines\kingsrow\engines\Kingsrow64.dll -Databases D:\tb\chinook
#>
param(
    [string] $SiteName = 'CheckersApi',
    [int] $Port = 8080,
    [string] $PhysicalPath = 'C:\inetpub\checkers-api',
    [string] $EnginePath = 'C:\engines\kingsrow\engines\Kingsrow64.dll',
    [string] $Databases = 'D:\tb\chinook',
    [int] $Workers = 2
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# --- prerequisites ---------------------------------------------------------------------------
if (-not (Get-Module -ListAvailable WebAdministration)) {
    throw 'IIS management cmdlets missing. Run: Install-WindowsFeature Web-Server, Web-Scripting-Tools, Web-AppInit'
}
if (-not (Test-Path "$env:ProgramFiles\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll")) {
    throw 'ASP.NET Core Module V2 not found. Install the .NET 10 Hosting Bundle, then run iisreset.'
}
if (-not (Test-Path $EnginePath)) { throw "KingsRow DLL not found at $EnginePath (see deploy\Install-Engine.ps1)." }
if (-not (Test-Path $Databases)) { throw "Chinook database folder not found at $Databases (see deploy\Install-Engine.ps1)." }
Import-Module WebAdministration

# --- publish ---------------------------------------------------------------------------------
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$staging = Join-Path $repo 'artifacts\publish'
dotnet publish (Join-Path $repo 'src\Checkers.Api') -c Release -o $staging
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$sitePath = "IIS:\Sites\$SiteName"
$poolPath = "IIS:\AppPools\$SiteName"
if (Test-Path $sitePath) { Stop-Website -Name $SiteName }
if ((Test-Path $poolPath) -and (Get-WebAppPoolState -Name $SiteName).Value -ne 'Stopped') {
    Stop-WebAppPool -Name $SiteName
    Start-Sleep -Seconds 3 # w3wp and its engine workers must release the files
}

New-Item -ItemType Directory -Force $PhysicalPath, (Join-Path $PhysicalPath 'logs') | Out-Null
robocopy $staging $PhysicalPath /MIR /XD logs /XF appsettings.Production.json /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE." }

@{
    Engine = @{ Type = 'chinook'; Path = $EnginePath; Databases = $Databases; Workers = $Workers }
} | ConvertTo-Json -Depth 3 | Set-Content -Encoding UTF8 (Join-Path $PhysicalPath 'appsettings.Production.json')

# --- app pool and site -----------------------------------------------------------------------
# A new pool starts at once; keep it stopped until its identity has been granted access to the
# engine and databases, or the first workers would fail with "access denied".
if (-not (Test-Path $poolPath)) { New-WebAppPool -Name $SiteName | Out-Null }
if ((Get-WebAppPoolState -Name $SiteName).Value -ne 'Stopped') { Stop-WebAppPool -Name $SiteName }
Set-ItemProperty $poolPath -Name managedRuntimeVersion -Value ''
Set-ItemProperty $poolPath -Name startMode -Value 'AlwaysRunning'
Set-ItemProperty $poolPath -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
Set-ItemProperty $poolPath -Name processModel.loadUserProfile -Value $true
Set-ItemProperty $poolPath -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)
Set-ItemProperty $poolPath -Name recycling.disallowOverlappingRotation -Value $true

if (-not (Test-Path $sitePath)) {
    New-Website -Name $SiteName -Port $Port -PhysicalPath $PhysicalPath -ApplicationPool $SiteName | Out-Null
} else {
    Set-ItemProperty $sitePath -Name physicalPath -Value $PhysicalPath
    Set-ItemProperty $sitePath -Name applicationPool -Value $SiteName
}
Set-ItemProperty $sitePath -Name applicationDefaults.preloadEnabled -Value $true

# --- permissions for the pool identity ---------------------------------------------------------
$identity = "IIS AppPool\$SiteName"
$engineRoot = Split-Path (Split-Path $EnginePath) # KingsRow's egdb64.dll lives one level above the engine DLL
icacls $PhysicalPath /grant "${identity}:(OI)(CI)RX" /Q | Out-Null
icacls (Join-Path $PhysicalPath 'logs') /grant "${identity}:(OI)(CI)M" /Q | Out-Null
icacls $engineRoot /grant "${identity}:(OI)(CI)RX" /Q | Out-Null
icacls $Databases /grant "${identity}:(OI)(CI)RX" /Q | Out-Null

# --- start and check -------------------------------------------------------------------------
if ((Get-WebAppPoolState -Name $SiteName).Value -ne 'Started') { Start-WebAppPool -Name $SiteName }
if ((Get-WebsiteState -Name $SiteName).Value -ne 'Started') { Start-Website -Name $SiteName }

$url = "http://localhost:$Port/healthz"
for ($attempt = 1; $attempt -le 30; $attempt++) {
    try {
        $health = Invoke-RestMethod $url -TimeoutSec 5
        if ($health.ok) {
            Write-Host "Healthy: $($health.workers)/$($health.configuredWorkers) workers, $($health.engineName)" -ForegroundColor Green
            Write-Host "Test board: http://localhost:$Port/   Acceptance: .\deploy\Test-Acceptance.ps1 -BaseUrl http://localhost:$Port"
            exit 0
        }
    } catch {
        Start-Sleep -Seconds 2
    }
}
throw "The site did not become healthy; see $PhysicalPath\logs\checkers-api-*.json and the Application event log."
