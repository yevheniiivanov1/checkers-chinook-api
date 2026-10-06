<#
.SYNOPSIS
    Runs the published API under IIS Express through the ASP.NET Core Module (ANCM V2) - the same
    in-process hosting path as full IIS, for machines without the IIS role (no admin rights needed).

.DESCRIPTION
    IIS Express ships ANCM V2 with the .NET Hosting Bundle, but its default configuration does not
    register it (Visual Studio generates its own). This script writes a private applicationhost.config
    under artifacts\iisexpress that registers the module and one site pointing at the publish folder,
    then starts IIS Express in the foreground. Ctrl+C or Q stops it.

.EXAMPLE
    .\deploy\Run-IISExpress.ps1 -Port 8085
#>
param(
    [string] $PublishDir = (Join-Path $PSScriptRoot '..\artifacts\publish'),
    [int] $Port = 8085,
    [switch] $NoPublish
)

$ErrorActionPreference = 'Stop'

$iisExpressHome = Join-Path $env:ProgramFiles 'IIS Express'
$iisExpress = Join-Path $iisExpressHome 'iisexpress.exe'
$ancm = Join-Path $iisExpressHome 'Asp.Net Core Module\V2\aspnetcorev2.dll'
if (-not (Test-Path $iisExpress)) { throw "IIS Express is not installed ($iisExpress)." }
if (-not (Test-Path $ancm)) { throw "ANCM V2 for IIS Express not found ($ancm). Install the .NET Hosting Bundle." }

if (-not $NoPublish) {
    dotnet publish (Join-Path $PSScriptRoot '..\src\Checkers.Api') -c Release -o $PublishDir
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
}
$PublishDir = (Resolve-Path $PublishDir).Path

$configDir = Join-Path $PSScriptRoot '..\artifacts\iisexpress'
New-Item -ItemType Directory -Force $configDir | Out-Null
$configPath = Join-Path (Resolve-Path $configDir).Path 'applicationhost.config'

[xml] $config = Get-Content (Join-Path $iisExpressHome 'config\templates\PersonalWebServer\applicationhost.config')

function Add-Element($parent, [string] $name, [hashtable] $attributes) {
    $element = $config.CreateElement($name)
    foreach ($key in $attributes.Keys) { $element.SetAttribute($key, $attributes[$key]) }
    [void] $parent.AppendChild($element)
    $element
}

# 1. The <aspNetCore> configuration section that web.config uses.
$webServerSections = $config.configuration.configSections.sectionGroup | Where-Object name -eq 'system.webServer'
[void] (Add-Element $webServerSections 'section' @{ name = 'aspNetCore'; overrideModeDefault = 'Allow' })

# 2. The native module itself, and enabling it.
[void] (Add-Element $config.configuration.'system.webServer'.globalModules 'add' @{ name = 'AspNetCoreModuleV2'; image = $ancm })
$rootLocation = $config.configuration.location | Where-Object { $_.path -eq '' }
[void] (Add-Element $rootLocation.'system.webServer'.modules 'add' @{ name = 'AspNetCoreModuleV2'; lockItem = 'true' })

# 3. A "No Managed Code" pool, as on IIS, and one site for the publish folder.
$appHost = $config.configuration.'system.applicationHost'
[void] (Add-Element $appHost.applicationPools 'add' @{ name = 'CheckersApi'; managedRuntimeVersion = ''; managedPipelineMode = 'Integrated' })
foreach ($site in @($appHost.sites.site)) { [void] $appHost.sites.RemoveChild($site) }
$site = $config.CreateElement('site')
$site.SetAttribute('name', 'CheckersApi')
$site.SetAttribute('id', '1')
[void] $appHost.sites.PrependChild($site)
$application = Add-Element $site 'application' @{ path = '/'; applicationPool = 'CheckersApi' }
[void] (Add-Element $application 'virtualDirectory' @{ path = '/'; physicalPath = $PublishDir })
$bindings = Add-Element $site 'bindings' @{}
[void] (Add-Element $bindings 'binding' @{ protocol = 'http'; bindingInformation = "*:${Port}:localhost" })

$config.Save($configPath)
Write-Host "IIS Express: http://localhost:$Port/  (site root $PublishDir)"
Write-Host "Config:      $configPath"
& $iisExpress "/config:$configPath" /site:CheckersApi /systray:false
