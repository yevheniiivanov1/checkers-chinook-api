# Checkers move API (Chinook databases via KingsRow)

An ASP.NET Core Web API, hosted in IIS, that takes an English-checkers position in PDN and returns
the engine's best move. Endgames of up to 8 pieces are answered from the Chinook endgame databases;
everything else is searched by KingsRow. A small web board at `/` plays against the service, so it
can be checked by hand.

Running instance: <https://checkers-chinook-api.azurewebsites.net/> — Azure App Service on Windows
(IIS, in-process), KingsRow 1.20 with the Chinook 2–7 piece databases, Free tier: after 20 minutes
without requests the app is unloaded, and the first request after that takes about 4 seconds.

```
 browser / client
       │  HTTP: POST /v1/move/suggest, /v1/move/validate, GET /healthz
       ▼
 IIS ─ w3wp.exe ─ ASP.NET Core (in-process, ANCM V2)
       │   validate PDN → LRU cache → worker pool (round robin, async lock per worker)
       │   → tablebase probe or search → legality check → JSON log line
       │
       ├── Checkers.EngineHost.exe #1 ─┐  long-lived worker processes, started and warmed up
       └── Checkers.EngineHost.exe #2 ─┤  at app start; JSON lines over stdin/stdout;
                                        │  tied to w3wp by a kill-on-close job object
                                        ▼
                         Kingsrow64.dll (CheckerBoard engine API, native x64)
                                        │  reads
                                        ▼
                         Chinook WLD databases DB6, DB7.x, DB8.x (2–8 pieces)
```

## Why KingsRow

Chinook (University of Alberta) is not distributed as a program; its 2–8 piece endgame databases
are. KingsRow (Ed Gilbert) is a free native Windows engine that implements the CheckerBoard engine
API and reads those databases directly — the "practical Windows path" the task describes. KingsRow
reports on startup which database it loaded (`Using the Chinook 7-piece WLD endgame database`), and
the API exposes that in `/healthz` and in every answer (`info.engineName`).

## API

### `POST /v1/move/suggest`

```json
{
  "gameId": "checkers-8x8",
  "state": { "notation": "PDN", "position": "B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16" },
  "level": "strong",
  "limits": { "maxDepth": 18, "softTimeMs": 500, "hardTimeMs": 1200 }
}
```

```json
{
  "engine": "chinook",
  "bestMove": "14x23",
  "pv": ["14x23", "27x18", "16x23", "25-21", "12-16", "28-24", "16-19", "24x15", "10x19", "22-17", "19-24"],
  "scoreOrWDL": 145,
  "depth": 17,
  "nodes": 153207,
  "positionKey": "pdn:B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16",
  "info": {
    "tablebaseHit": false, "timeMs": 32, "cached": false, "level": "strong",
    "engineName": "Kingsrow(x64) 1.20 + Chinook 7-piece WLD", "score": 145, "engineTimeMs": 29, "worker": 1
  }
}
```

- `scoreOrWDL`: on a tablebase hit 1 / 0 / −1 (win / draw / loss for the side to move); otherwise the
  search score for the side to move, a man = 100.
- Moves use PDN notation: `22-18`, `14x23`, and the full path for a multi-jump (`6x15x24`). The
  example response in the task (`22-18x11-7`) is not a single PDN move; the example position also has
  a compulsory capture (Black must play `14x23` or `16x23`).
- `pv` is replayed through the rules engine and cut at the first move that does not fit.
- `depth` is the last completed iteration. On a tablebase hit it is the depth KingsRow's 10 ms probe
  reached (so it can be well outside the level's band, e.g. 21 for a weak request), and for a forced
  move KingsRow answers at once with a shallow depth.
- Fields in `info` beyond `tablebaseHit` and `timeMs` are additions.
- `Cache-Control: no-cache` makes the service search again instead of answering from its cache.

### `POST /v1/move/validate`

`{ "position": "B:W21-32:B1-12", "move": "11-15" }` → `{ "legal": true, "move": "11-15", "resultPosition": "W:W21,…:B1,…,15" }`.
An illegal move gives `{ "legal": false, "reason": "A capture is compulsory. Legal moves: 14x23, 16x23." }`.
Steps are written with `-`, captures with `x` (or `:`); `6-24` for the capture `6x15x24` is not legal
and the reason says so. A capture may be given by its full path (`22x15x6`) or start and end only
(`22x6`); when the short form fits several routes the answer is
`{ "legal": true, "ambiguous": true, "candidates": ["22x13x6", "22x15x6"], … }` without a `resultPosition`.

### `GET /healthz`

`{ "ok": true, "workers": 2, "configuredWorkers": 2, "engine": "chinook", "engineName": "…", "tablebasePieces": 7, "cacheEntries": 0 }`
— 200 while at least one worker is ready, 503 otherwise.

### `POST /v1/position/moves` (extra, used by the test board)

All legal moves of a position with their paths, captured squares and resulting positions.

### Status codes

| Code | When |
|------|------|
| 200 | move found. The worker's own hard limit is 10 % below the request's, so a search that runs long still answers in time with its best move so far |
| 400 | malformed JSON |
| 422 | invalid PDN (all problems listed), unknown level, wrong `gameId`/`notation`, limits out of range (`softTimeMs` ≤ 2000, `hardTimeMs` ≤ 5000) or contradictory, game already over |
| 429 | the client already has 4 suggestions running (`RateLimit` section); `Retry-After: 1` |
| 500 | the engine produced no legal move (its best move and the first pv move are both checked) |
| 503 | no engine worker running |
| 504 | no answer within `hardTimeMs` (e.g. every worker busy) |

Errors are `application/problem+json` and carry `requestId`, which is also returned in the
`X-Request-Id` header. A caller may send its own id (1–64 characters of `[A-Za-z0-9._-]`; anything
else is replaced).

## How a request is handled

1. **Parse and normalise PDN** (`Checkers.Core/Pdn.cs`): `[FEN "…"]` tags, ranges (`W21-32`), case and
   colour order are accepted; squares 1–32, duplicates, more than 12 pieces a side, uncrowned men on
   the last rank and empty boards are rejected. The canonical form is the cache key and `positionKey`.
2. **Tablebase first**: with at most `min(Engine:TablebaseMaxPieces, installed database size)` pieces,
   KingsRow gets a 10 ms budget with its database enabled. A definite win/draw/loss is returned
   immediately with `tablebaseHit: true`. KingsRow reports database draws in a separate status format
   (and often returns "unknown" from `getmove()` for them); both are handled.
3. **Search** otherwise, within the level's limits (below).
4. **Legality check**: the engine's move is matched against the rules engine's legal moves. If it is not
   legal, the first pv move is tried — the only other move the engine proposed for this position (later
   pv moves belong to later positions) — then 500. If the worker dies during the request, it is retried
   once on another worker when the deadline allows.
5. **Cache** (LRU, 20 000 entries, 15 min) by canonical PDN + level + effective limits. An answer cut
   short by the hard limit, or by a soft limit the remaining time had shortened, is returned but not
   cached.

### Levels

| Level | Depth | Move time | Notes |
|-------|-------|-----------|-------|
| weak | 6–8 | 100 ms | no randomness (see Determinism) |
| medium | 10–12 | 250 ms | |
| strong | 14–18 | 500 ms | tablebase first (as every level, per the request flow) |

A level is a band; `limits` can only narrow it (a weak request with `maxDepth: 12` still stops at 8).
Without `level` the request's limits apply, falling back to the `Limits` section.

The CheckerBoard API has no depth limit, so the worker enforces both limits itself: a monitor thread
reads the status line KingsRow keeps rewriting (`value=115, depth 14/15.1/25, 0.0s, 7149 kN/s, pv …`)
and raises `playnow` when an iteration deeper than `maxDepth` starts, or when `softTimeMs` has passed
and `minDepth` is complete. KingsRow deepens two plies at a time, so the reported depths are 7, 11 and
17. KingsRow reports speed rather than a node count, so `nodes` = kN/s × search time.

### Determinism

Nothing random takes part in a search: the opening book is off, `dither` (an undocumented KingsRow
setting that by its name adds noise) is 0, each worker searches with one thread, every `getmove()` call
carries the "reset moves" flag, and weak stops on depth rather than time. Because KingsRow keeps its
settings in the registry, all of these are set explicitly when a worker starts.

What remains is KingsRow's transposition table, which lives as long as the worker. Entries left by
earlier searches can change a weak answer's score by up to about a tenth of a man (86–98 for the same
position in one test run) and, when two moves score the same, which of them is chosen. Measured on KingsRow 1.20: 400 of 400 repeated weak searches over five
positions chose the same move; with strong searches interleaved through the API, an occasional
equal-score alternative appeared (e.g. 21-17 and 23-18, both −116). The table can only be cleared by
re-sizing it, and that makes KingsRow re-read its endgame databases on the next search: 150–330 ms per
request (measured), longer than a weak move is allowed to take. So it is not cleared; identical
requests within the cache TTL get identical answers.

## Engine workers (`Checkers.EngineHost`)

- Separate x64 processes: KingsRow keeps global state (one search at a time), a native crash cannot take
  down w3wp, and the API itself can run in a 32-bit pool.
- Created once at startup (2 by default) and warmed up — engine init, database drivers (≈ 350 ms on
  first search), JIT — before the server accepts requests; then a few real HTTP calls warm the MVC path.
  Nothing is spawned per request.
- Protocol: one JSON object per line. `stop` interrupts only the search whose id it names. The original
  stdout handle is kept for the protocol and the process stdout is pointed at stderr, so anything the
  DLL prints goes to the log instead of corrupting a reply.
- Routing: round robin, skipping busy workers; when all are busy the request queues on the round-robin
  choice (async lock per worker). `hardTimeMs` covers the wait.
- A search cancelled by the deadline is told to stop; the worker stays locked until it answers (so a
  late reply is never read as the next request's), or is killed after 500 ms.
- A worker that dies is restarted with back-off (1 s → 30 s); `/healthz` shows the count meanwhile.
  It is taken out of rotation before the waiting request hears about the failure, so a retry cannot
  land on it again.
- A worker that is pointed at a database folder but loads no database (wrong folder, or a non-ASCII path,
  which the 8-bit `enginecommand()` would garble) fails to start, so `/healthz` reports it instead of
  the service quietly answering without tablebases.
- KingsRow writes its own log under `SHGetFolderPathA(CSIDL_PERSONAL)` — Documents — and ends the
  process (0xC0000409) if it cannot. An IIS app pool identity gets no Documents folder from Windows, even
  with its profile loaded, so under IIS every worker died two seconds after starting. The worker rewrites
  that one import in the loaded DLL's import address table so Documents means `logs\kingsrow`, next to
  the API's logs; other folder requests pass through.
- All workers are in a Windows job object with kill-on-close: an app pool recycle or crash takes them
  down with w3wp. The pool waits for a graceful stop before closing the job.

## Logging

One JSON line per request (Serilog, compact log event format), to stdout and to
`logs\checkers-api-<date>.json`, which roll daily and at 100 MB, 14 files kept (`LogFiles` section).
The ASP.NET Core Module's own stdout log is off: it never rolls and is meant for diagnosing start-up.
KingsRow's own logs (settings, database loading, each search) are in `logs\kingsrow`.

```json
{"@t":"2026-10-06T18:47:16.3591826Z","@mt":"{Method} {Path} -> {StatusCode} in {TimeMs} ms; requestId={RequestId} …",
 "RequestId":"55d7133947544ab9b2e92039a54ce20f","Method":"POST","Path":"/v1/move/suggest","StatusCode":200,"TimeMs":22,
 "Depth":13,"Nodes":53770,"TablebaseHit":true,"Cached":false,"Level":"strong","Worker":1,"BestMove":"25-21",
 "EventId":{"Id":1,"Name":"Request"},"SourceContext":"Checkers.Api.Logging.RequestLogMiddleware"}
```

## Running it

Prerequisites: Windows, .NET 10 SDK.

```powershell
# KingsRow + Chinook databases (-MaxPieces 6 | 7 | 8: 28 MB | 230 MB | 2.7 GB)
.\deploy\Install-Engine.ps1 -EngineDir C:\engines\kingsrow -DatabaseDir D:\tb\chinook -MaxPieces 8

dotnet run --project src\Checkers.Api      # http://localhost:5093/ — the test board
```

Paths are in `src/Checkers.Api/appsettings.json` (`Engine:Path` is the DLL:
`C:\engines\kingsrow\engines\Kingsrow64.dll`). Without KingsRow, set `Engine:Type` to `builtin`: a
small alpha-beta engine in the same worker process, so everything else works the same (answers say
`"engine": "builtin"`).

### IIS

```powershell
# Windows Server
Install-WindowsFeature Web-Server, Web-Scripting-Tools, Web-AppInit
# Windows 10/11
Enable-WindowsOptionalFeature -Online -All -FeatureName IIS-WebServerRole, IIS-WebServer, IIS-ManagementScriptingTools, IIS-ApplicationInit

# then install the .NET 10 Hosting Bundle (if it was installed before IIS, run it again and Repair), and:
iisreset
.\deploy\Install-IIS.ps1 -Port 8080                 # publish, app pool, site, permissions, health check
.\deploy\Test-Acceptance.ps1 -BaseUrl http://localhost:8080
```

`Install-IIS.ps1` creates a "No Managed Code" pool with `AlwaysRunning`, no idle timeout, no periodic
or overlapped recycle (an overlapped recycle would briefly double the engine workers), user profile
loaded (KingsRow keeps settings in HKCU), preload enabled, and grants the pool identity read access to
the engine and database folders and write access to `logs\`. `Install-Engine.ps1` runs the KingsRow
installer only if its SHA-256 matches the pinned one.

Without the IIS role (no admin rights needed), `.\deploy\Run-IISExpress.ps1` runs the same publish output
under IIS Express with the same ASP.NET Core Module, in-process.

### Azure App Service (Windows)

```powershell
az login
.\deploy\Deploy-Azure.ps1 -AppName checkers-chinook-api -Location polandcentral -Sku F1
.\deploy\Test-Acceptance.ps1 -BaseUrl https://checkers-chinook-api.azurewebsites.net -ServerTime
```

App Service on Windows is IIS with the same module, so the app runs unchanged; the script only sets
paths through app settings. KingsRow and the databases go to `D:\home\data` (outside `wwwroot`, so a code
deploy does not re-upload them; `-SkipData` skips them), logs to `D:\home\LogFiles\checkers-api`. Two
things differ from a server of your own: the sandbox does not allow registry writes, which KingsRow
survives (every setting is sent at start anyway), and it gives the app no Documents folder, which the
log redirect above already covers. The Free tier runs a 32-bit w3wp, so the worker is published
self-contained for x64; it also has 1 GB of memory for everything, so the engine hash and database cache
are reduced to 32 and 64 MB per worker. `-ServerTime` judges the time limits by the time the service
reports, since the round trip from the client alone can be longer than 50 ms.

## Verified

Deployed with `deploy\Install-IIS.ps1` to IIS 10 on Windows 11 Pro (app pool identity
`IIS AppPool\CheckersApi`, KingsRow 1.20, Chinook 2–7 piece databases), then checked with
`deploy\Test-Acceptance.ps1` — times measured by the client, round trip included; the cache is
bypassed and every answer checked to be `cached: false`, so repeated runs measure the engine:

| Criterion | Result |
|-----------|--------|
| Health check ok on startup | 200, 2/2 workers |
| Tablebase ≤ 8 pieces < 50 ms, `tablebaseHit` | 7–24 ms over four positions |
| Midgame, strong, < 600 ms, legal | 89–153 ms, depth 17 |
| Invalid PDN → 422 | 422 |
| Timeout → 504 | 504 at 205 ms for `hardTimeMs: 200` with both workers busy |

Also under IIS: `Restart-WebAppPool` closed the old workers' stdin (they logged their exit), and the new
w3wp had two warm workers 1.3 s later, the site answering 503 for about 2 s in between (overlapped
recycle is off). `Stop-Process w3wp -Force` took both workers down with it through the job object, and
IIS started a new w3wp with new workers straight away (`AlwaysRunning`). The same acceptance run passes
under IIS Express (`Run-IISExpress.ps1`).

The same checks against the Azure instance above (F1, Poland Central, 32/64 MB engine caches), by the
service's own `info.timeMs`; the client saw about 50 ms more per request (the round trip):

| Criterion | Result |
|-----------|--------|
| Health check ok on startup | 200, 2/2 workers |
| Tablebase ≤ 7 pieces < 50 ms, `tablebaseHit` | 16–25 ms |
| Midgame, strong, < 600 ms, legal | 124–296 ms, depth 17–19 (177–350 ms at the client) |
| Invalid PDN → 422 | 422 |
| Timeout → 504 | 504 at 306 ms at the client for `hardTimeMs: 200` with both workers busy |

Not verified here:

- **Windows Server.** The IIS deployment above ran on Windows 11 Pro; `Install-IIS.ps1` uses the same
  IIS cmdlets on Server, where the role is added with `Install-WindowsFeature` instead.
- **8-piece databases.** Only the 2–7 piece files are installed here, so 8-piece positions are searched,
  not looked up, and the 50 ms target is confirmed for up to 7 pieces. The 8-piece set is 5.6 GB; with
  `DbCacheMb = 256` per worker, cold lookups would read from disk.

## Tests

`dotnet test` — 192 tests:

- **Core** (103): perft from the initial position to depth 8 (845 931, the published count), PDN parsing
  and every validation rule (including non-ASCII digits), captures, multi-jumps, crowning, notation and
  separators; KingsRow status-line parsing on real output, board conversion against `cb_interface.h`,
  game-value rules; the builtin engine and its stop reasons.
- **API** (89), against real worker processes: the HTTP contract, 422/400/429 cases, cache and cache
  bypass, request ids; 503/500/504, pv fallback, retry after a worker failure and caching of cut-short
  answers through a pool double; round robin, queueing, cancellation, crash restart and shutdown of the
  process pool; LRU and level policy units.
- **Acceptance** (16 of the 89): the task's criteria against KingsRow + Chinook, weak-level
  reproducibility, a database folder KingsRow cannot use, and where KingsRow writes its log. They run when the DLL and databases are
  found (`CHECKERS_ENGINE_PATH`, `CHECKERS_DATABASES`, defaults as above) and are skipped otherwise,
  e.g. in CI.

## Possible next steps

- **Probe the database directly.** `egdb64.dll`, the driver KingsRow ships, exports `egdb_open` with a
  lookup call: an exact WDL in microseconds instead of a 10 ms search, choosing among value-preserving
  moves by the children's values. Positions with a capture pending are not stored in the Chinook files,
  so those would still need a small search.
- **A separate lane for tablebase positions**, so long searches cannot delay them; today they queue for
  the same two workers.
- **Single-flight** for identical concurrent requests, which now search twice.

## Layout

```
src/Checkers.Core          rules: PDN, move generation, notation; worker protocol records
src/Checkers.EngineHost    worker process: protocol loop, KingsRow adapter, builtin engine
src/Checkers.Api           controllers, worker pool, cache, logging, wwwroot (test board), web.config
tests/                     Checkers.Core.Tests, Checkers.Api.Tests
deploy/                    Install-Engine, Install-IIS, Run-IISExpress, Deploy-Azure, Test-Acceptance
```
