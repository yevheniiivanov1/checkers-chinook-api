<#
.SYNOPSIS
    Checks the task's acceptance criteria against a running instance (IIS, IIS Express or Kestrel).

.DESCRIPTION
    1. Health check returns ok.
    2. A tablebase position (<= the installed database size) returns in under 50 ms with tablebaseHit.
    3. A midgame position at level strong returns in under 600 ms with a legal move.
    4. Invalid PDN gets 422.
    5. Timeouts return 504 (all workers are kept busy, then a request with a short hardTimeMs is sent).
    Times are measured by this client, round trip included; with -ServerTime the time limits are checked
    against the time the service reports (info.timeMs) instead, for an instance across the internet where
    the round trip alone can exceed 50 ms. Both are shown. Every request sends Cache-Control: no-cache,
    which makes the service search again instead of answering from its 15-minute cache, and the timing
    checks also require info.cached = false, so a second run within 15 minutes measures the engine too.
    Exit code 0 when everything passes.

.EXAMPLE
    .\deploy\Test-Acceptance.ps1 -BaseUrl http://localhost:8085

.EXAMPLE
    .\deploy\Test-Acceptance.ps1 -BaseUrl https://checkers-chinook-api.azurewebsites.net -ServerTime
#>
param([string] $BaseUrl = 'http://localhost:8085', [switch] $ServerTime)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
# Windows PowerShell allows two connections per remote host (loopback is unlimited), which would hold the
# timeout check's request back until one of the long searches finished.
[Net.ServicePointManager]::DefaultConnectionLimit = 32
$client = New-Object System.Net.Http.HttpClient
$client.BaseAddress = [Uri] $BaseUrl
$client.Timeout = [TimeSpan]::FromSeconds(30)
$client.DefaultRequestHeaders.CacheControl = New-Object System.Net.Http.Headers.CacheControlHeaderValue -Property @{ NoCache = $true }
$results = New-Object System.Collections.Generic.List[object]

function Send-Json([string] $path, [string] $json) {
    $content = New-Object System.Net.Http.StringContent($json, [Text.Encoding]::UTF8, 'application/json')
    return $client.PostAsync($path, $content)
}

function Invoke-Timed([string] $path, [string] $json) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $response = (Send-Json $path $json).Result
    $body = $response.Content.ReadAsStringAsync().Result
    $clock.Stop()
    [pscustomobject] @{ Status = [int] $response.StatusCode; Ms = $clock.ElapsedMilliseconds; Body = $(if ($body) { $body | ConvertFrom-Json } else { $null }) }
}

function Suggest-Json([string] $position, [string] $level, [string] $limits = $null) {
    $limitsJson = if ($limits) { ",`"limits`":$limits" } else { '' }
    $levelJson = if ($level) { ",`"level`":`"$level`"" } else { '' }
    "{`"gameId`":`"checkers-8x8`",`"state`":{`"notation`":`"PDN`",`"position`":`"$position`"}$levelJson$limitsJson}"
}

function Get-Ms($r) { if ($ServerTime) { [int] $r.Body.info.timeMs } else { $r.Ms } }

function Format-Ms($r) { "client $($r.Ms) ms, server $($r.Body.info.timeMs) ms" }

function Test-Legal([string] $position, [string] $move) {
    $check = Invoke-Timed '/v1/move/validate' "{`"position`":`"$position`",`"move`":`"$move`"}"
    return $check.Body.legal
}

function Add-Result([string] $criterion, [bool] $pass, [string] $detail) {
    $results.Add([pscustomobject] @{ Result = $(if ($pass) { 'PASS' } else { 'FAIL' }); Criterion = $criterion; Detail = $detail })
}

# 1. Health
$health = $client.GetAsync('/healthz').Result
$healthBody = $health.Content.ReadAsStringAsync().Result | ConvertFrom-Json
Add-Result 'Health check returns ok' ([int] $health.StatusCode -eq 200 -and $healthBody.ok) `
    "HTTP $([int] $health.StatusCode), workers $($healthBody.workers)/$($healthBody.configuredWorkers), $($healthBody.engineName)"
$workers = [Math]::Max(1, [int] $healthBody.workers)

# 2. Tablebase positions (7 pieces or fewer, so they fit the 2-7 piece database as well as 2-8)
foreach ($position in 'W:W18,22,25,30:B1,5,10', 'W:WK14,K22:BK1', 'W:WK10,K14:BK1,K3', 'B:W18,22,25:B1,5,10,14') {
    $r = Invoke-Timed '/v1/move/suggest' (Suggest-Json $position 'strong')
    $pass = $r.Status -eq 200 -and $r.Body.info.tablebaseHit -and -not $r.Body.info.cached -and (Get-Ms $r) -lt 50 -and (Test-Legal $position $r.Body.bestMove)
    Add-Result "Tablebase <= 8 pieces: < 50 ms, tablebaseHit" $pass `
        "$position -> $($r.Body.bestMove), WDL $($r.Body.scoreOrWDL), tablebaseHit $($r.Body.info.tablebaseHit), cached $($r.Body.info.cached), $(Format-Ms $r)"
}

# 3. Midgame, level strong
foreach ($position in 'B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16', 'W:W17,21,22,23,25,26,27,28,30:B1,2,3,5,6,7,9,11,12,15', 'B:W21-32:B1-12') {
    $r = Invoke-Timed '/v1/move/suggest' (Suggest-Json $position 'strong')
    $pass = $r.Status -eq 200 -and -not $r.Body.info.cached -and (Get-Ms $r) -lt 600 -and (Test-Legal $position $r.Body.bestMove)
    Add-Result 'Midgame strong: < 600 ms, legal move' $pass `
        "$position -> $($r.Body.bestMove), depth $($r.Body.depth), nodes $($r.Body.nodes), cached $($r.Body.info.cached), $(Format-Ms $r)"
}

# 4. Invalid PDN
$r = Invoke-Timed '/v1/move/suggest' (Suggest-Json 'B:W18,19,22,25,27,28,30,33:B1,5,6,7,10,12,14,16' 'strong')
Add-Result 'Invalid PDN gets 422' ($r.Status -eq 422) "HTTP $($r.Status): $(($r.Body.errors.PSObject.Properties | ForEach-Object { $_.Value }) -join ' ')"

# 5. Timeout: keep every worker busy with a long search, then ask with a short hard limit.
$longSearches = 'B:W21-32:B1-12', 'W:W17,21,22,23,25,26,27,28,30:B1,2,3,5,6,7,9,11,12,15', 'B:W18,21,23,24,26,27,28,29,30,31,32:B1,2,3,4,5,6,7,8,9,10,12', 'W:W21-32:B1-8,10,11,12,15'
$busy = 0..($workers - 1) | ForEach-Object {
    Send-Json '/v1/move/suggest' (Suggest-Json $longSearches[$_ % $longSearches.Count] $null "{`"maxDepth`":64,`"softTimeMs`":$(1500 + $_),`"hardTimeMs`":3000}")
}
Start-Sleep -Milliseconds 400
$r = Invoke-Timed '/v1/move/suggest' (Suggest-Json 'B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16' 'medium' '{"hardTimeMs":200}')
Add-Result 'Timeouts return 504' ($r.Status -eq 504) "HTTP $($r.Status) after $($r.Ms) ms with hardTimeMs=200 while $workers worker(s) busy"
[void] [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]] $busy, 10000)

$results | Format-Table -AutoSize -Wrap
$failed = @($results | Where-Object Result -eq 'FAIL').Count
if ($failed -gt 0) { Write-Host "$failed check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All acceptance checks passed.' -ForegroundColor Green
