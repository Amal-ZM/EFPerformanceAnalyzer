# EF Performance Analyzer

A .NET Core 8 Web API + SQL Server tool with a browser dashboard that scans any C# codebase on
disk and reports fifteen categories of performance anti-pattern, ranked by severity and pinned to
an exact file and line.

**EF-model-aware** — need a DbContext to say anything:

- **N+1 queries** — navigation property accessed inside a loop without `Include()`
- **Missing `AsNoTracking()`** — materialized read-only queries that still pay change-tracking overhead
- **Missing `Include()`** — a single fetched entity's navigation is dereferenced without eager loading
- **Unused navigation properties** — mapped relationships nobody ever reads
- **Multiple `SaveChanges()`** — a method that round-trips to the database more than once when it could batch

**Query shape** — read off the fluent LINQ chain hanging from a `DbSet`:

- **Client-side evaluation** — `.ToList().Where(...)` loads the whole table, then filters in memory
- **Query inside a loop** — a fresh round trip on every iteration
- **`SaveChanges()` inside a loop** — one transaction per row instead of one batch
- **Unbounded query** — no `Where` filter and no `Skip`/`Take`, so cost grows with the table
- **Cartesian `Include`** — stacked `Include`s in one JOIN multiply the rows returned; wants `AsSplitQuery()`
- **`Count()` as an existence check** — `SELECT COUNT(*)` where `Any()` would compile to `EXISTS`

**General .NET throughput** — no EF required, these fire on any C#:

- **Sync-over-async** — `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` block a thread-pool thread
- **`async void`** — the caller can neither await it nor catch what it throws
- **String `+=` in a loop** — reallocates and copies the accumulated string every pass
- **Blocking call in an async method** — `Thread.Sleep` parks the thread instead of yielding it

It scans **any** C#/.NET codebase directly off disk — it does not require the target to build,
restore its NuGet packages, or use a specific project layout. It only needs `.cs` files.
This is why the engine parses syntax trees directly (Roslyn, no MSBuild/semantic compilation)
rather than opening the target as a loaded solution.

## What it does and doesn't understand

The eleven EF-specific detectors key off EF Core / C# APIs — `AsNoTracking()`, `Include()`,
`SaveChanges()`, `DbSet<T>`. Those don't exist in other ORMs, so pointing the tool at a Dapper or
raw-ADO.NET codebase correctly reports zero DbContexts and zero findings from that group. The four
general-throughput detectors have no such dependency and apply to any C# file.

It is not a multi-language linter — it reads `.cs` only, and knows nothing about JavaScript,
TypeScript, or SQL files.

## Architecture

```
src/EFPerformanceAnalyzer.Core       Roslyn-based scanning engine (no external dependencies at scan time)
src/EFPerformanceAnalyzer.Api        ASP.NET Core 8 Web API + EF Core persistence of scan history (SQL Server or Postgres)
src/EFPerformanceAnalyzer.Api/wwwroot  The dashboard: three static files, no build step
samples/SampleTarget                 An EF Core project with one deliberate instance of every anti-pattern
samples/SuppressionTest              A minimal project demonstrating ef-analyzer-ignore and the run diff
Dockerfile                           Multi-stage build for deployment (see "Deploying it" below)
render.yaml                          Render Blueprint: provisions the free web service + Postgres together
```

The dashboard is deliberately plain HTML/CSS/JS served by the API itself, so `dotnet run` starts
both on one origin — no second process, no npm install, no CORS configuration, and nothing to
rebuild after editing it. Swagger/OpenAPI is still enabled in Development, which keeps the contract
self-describing for any script or agent calling it directly (`GET /swagger/v1/swagger.json`).

## Running it

Requires .NET 8 SDK and a reachable SQL Server instance.

```bash
cd src/EFPerformanceAnalyzer.Api
dotnet run --launch-profile http
```

Then open **http://localhost:5012** for the dashboard (Swagger is at `/swagger`). On startup the app
calls `Database.EnsureCreated()` against the connection string in `appsettings.json`
(`ConnectionStrings:DefaultConnection`) — no migrations step needed for the simple two-table schema
(`AnalysisRuns`, `Findings`).

### Using the dashboard

Paste a project path and hit **Analyze**. Because the engine reads source straight off disk, this
works against a project open in any editor — VS Code, Visual Studio, Rider, WebStorm — with no
plugin, and the target doesn't even have to compile.

Results are ordered most-severe-first, and each finding names the file, the line, and the
containing member, with a copy-path button and a `vscode://` deep link so you can jump straight to
it. The **Hotspots** panel ranks files by weighted severity (Critical 10 / Warning 3 / Info 1) —
click one to filter to it. Severity checkboxes are the sensitivity control: all three are on by
default (the most sensitive setting), so untick Info and Warning when you only want the
five-alarm items. Three **Export** links (SARIF / CSV / Markdown) sit next to the severity filters
once a run is loaded.

No local path to point at? The **Upload a folder** tab takes a folder straight from the browser —
click to browse or drag one in — with no zip step; only `.cs` files leave your machine. **Upload a
.zip** covers the same case for a project already archived. The **History** tab adds a findings-per-run
trend chart and a baseline/current compare picker (see the diff endpoint below) once more than one
scan has run.

### Configuration (`appsettings.json`)

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=EFPerformanceAnalyzer;Trusted_Connection=True;TrustServerCertificate=True;"
  },
  "ScanSettings": {
    "AllowedRoots": [ "C:\\TechProcess\\data base" ],
    "MaxFilesPerScan": 100000,
    "ScanTimeoutSeconds": 1800,
    "MaxUploadSizeBytes": 1073741824,
    "MaxUploadExpansionRatio": 5
  }
}
```

**`ScanSettings:AllowedRoots` is a security boundary, not a convenience default.** This API accepts
a filesystem path over HTTP and reads whatever `.cs` files it finds there — without an allowlist that
is an arbitrary file-disclosure primitive. Requests for a `targetPath` outside every configured root
are rejected with 400, regardless of `..` traversal tricks (the path is resolved to its full form
before the check). If you deploy this beyond localhost, tighten `AllowedRoots` to only the
directories you intend to let it read (or empty it entirely — a remote container's filesystem has no
paths worth scanning anyway, see below) and turn on `BASIC_AUTH_USERNAME`/`BASIC_AUTH_PASSWORD`.

`MaxFilesPerScan` and `ScanTimeoutSeconds` bound the cost of a single scan request.

## Deploying it (Render, free tier)

Local path scanning obviously doesn't make sense once this runs on a server that isn't your own
machine — deployed, it's meant to be used through **Upload a folder** or **Upload a .zip**.
`appsettings.Production.json` reflects that: `AllowedRoots` is empty (path-based scanning refuses
every request, fail-closed) and upload/file-count limits are turned down to fit the free tier's
512 MB RAM.

The repo is set up for Render's **Blueprint** flow, which provisions both pieces from one file
([render.yaml](render.yaml)) instead of clicking through settings by hand:

1. Push this repo to your own GitHub account (already done if you're reading this from there).
2. In the [Render dashboard](https://dashboard.render.com), **New → Blueprint**, and point it at
   the repo. Render reads `render.yaml` and provisions two resources together:
   - a **free Postgres database**
   - a **free web service**, built from [Dockerfile](Dockerfile), with `DATABASE_URL` wired
     straight from the database resource — nothing to copy-paste
3. Render also generates a random `BASIC_AUTH_PASSWORD` for you (username defaults to `admin`,
   change it in the service's Environment tab if you want). **Open the service's Environment tab
   and copy that generated password before you share the URL with anyone** — it's the only thing
   stopping a stranger who finds the link from uploading code and browsing your scan history.
4. First deploy takes a few minutes (Docker build + Postgres provisioning). After that, every push
   to your default branch redeploys automatically.

Two free-tier realities worth knowing going in: the instance **spins down after 15 minutes idle**
and takes ~30–50s to wake back up on the next request (nothing is wrong — that's the free plan, not
a bug), and Render's edge proxy has its own request timeout well under our own `ScanTimeoutSeconds`
default of 1800s, which is why Production dials that down to 80s — a genuinely huge codebase may
need to be scanned locally instead.

Why Postgres and not SQL Server: free managed SQL Server isn't offered by Render (or most PaaS
hosts) the way free Postgres is. `Database:Provider` in config picks the EF Core provider —
`SqlServer` (default, unchanged local dev flow) or `Postgres` — and `Program.cs` translates
Render's `DATABASE_URL` (a `postgres://` URI) into the connection string Npgsql expects.

## API contract

### `POST /api/analysis/scans`
For a project already on this machine's filesystem, under a configured `AllowedRoots` entry.
```json
{ "targetPath": "C:/path/to/your/project" }
```
Runs a scan synchronously, persists it, and returns a summary:
```json
{
  "runId": 2,
  "targetPath": "...",
  "filesScanned": 3,
  "dbContextsFound": 1,
  "entityTypesFound": 5,
  "totalFindings": 8,
  "findingsByCategory": { "NPlusOneQuery": 1, "MissingAsNoTracking": 3, ... }
}
```
400 if `targetPath` is missing, unresolvable, outside `AllowedRoots`, doesn't exist, or exceeds
`MaxFilesPerScan`.

### `POST /api/analysis/scans/upload`
For any project, from anywhere — no filesystem access to the API host required. Zip the project
and upload it as multipart form data:
```bash
curl -X POST http://localhost:5012/api/analysis/scans/upload -F "file=@myproject.zip"
```
```powershell
Invoke-RestMethod -Uri "http://localhost:5012/api/analysis/scans/upload" -Method Post -Form @{ file = Get-Item "myproject.zip" }
```
The archive is extracted into an isolated temp directory (protected against zip-slip and
decompression bombs — see `ScanSettings:MaxUploadSizeBytes` / `MaxUploadExpansionRatio`), scanned,
and deleted immediately afterward; nothing from the upload persists beyond the findings themselves.
Returns the same summary shape as `/scans`, with `targetPath` set to `upload:<your filename>` and
finding file paths relative to the archive root (e.g. `Models.cs`, not a server temp path). Rejects
non-`.zip` files and anything over `MaxUploadSizeBytes` (default 50MB) with 400.

### `POST /api/analysis/scans/upload-folder`
For a project picked directly in the browser's **Upload a folder** tab — a folder picker or
drag-and-drop, no zip step. Each file is sent as its own multipart part carrying its relative path
as the filename (`files=@Foo.cs;filename=Project/Sub/Foo.cs`). Client-side, the dashboard already
filters to `.cs` files and skips `bin`/`obj`/`node_modules`/`.git`/`.vs`; the server re-checks both,
plus the same path-containment guard as the zip upload (a relative path can smuggle `../` just as
easily as a zip entry name can).

### `GET /api/analysis/runs`
Last 100 scan summaries, most recent first. Each summary includes `suppressedCount` — findings that
matched a detector but were silenced by an `ef-analyzer-ignore` comment (see below).

### `GET /api/analysis/runs/{runId}`
Full detail for one run, including every finding: category, severity, file, line, containing
member, a one-line code snippet, a human-readable message, and a recommendation.

### `GET /api/analysis/runs/{baselineRunId}/diff/{currentRunId}`
Compares two runs of the same project: what's new since the baseline, what got resolved, and how
many findings persisted unchanged. Findings are matched on `(category, file, line, member)` — the
closest thing to a stable identity a heuristic scanner can offer, since findings carry no ID across
scans. A consequence: inserting or deleting lines above a finding shifts its line number and reads
as "resolved" + "new" rather than "unchanged", even though nothing about that specific issue
changed. Diff by re-scanning after a real fix, not after unrelated edits to the same file, for a
clean signal. The dashboard's **History** tab exposes this as a side-by-side compare picker,
defaulting to the two most recent runs.

### `GET /api/analysis/runs/{runId}/export/{format}`
`format` is one of `sarif`, `csv`, or `md`. SARIF 2.1.0 is what GitHub Code Scanning and VS Code's
Problems panel both read natively — wire it into CI and findings show up as PR annotations instead
of requiring someone to open this dashboard. CSV is for a spreadsheet; Markdown for pasting into a
ticket or PR description. All three are also one-click downloads from the dashboard once a run is
loaded.

## Silencing a finding

A specific line can be exempted with a comment — no separate ignore file to keep in sync with the
code it applies to:

```csharp
return context.Students.ToList(); // ef-analyzer-ignore: MissingAsNoTracking
```

Omit the category to silence everything the scanner would otherwise flag on that line:

```csharp
// ef-analyzer-ignore
return context.Students.ToList();
```

The comment works both trailing the flagged line and alone on the line directly above it (the
`eslint-disable-next-line` convention), so it reads naturally whichever fits the code better.
Suppressed findings are still counted — `SuppressedCount` on the run summary and a **Suppressed**
tile in the dashboard's stat grid — so a suppression is visible, not silently gone.

## Detection approach (and its limits)

The engine builds an approximate EF model (DbContexts → DbSets → entity types → navigation
properties) purely from class/property syntax — no compilation, so it works even on code that
doesn't currently build. It then walks each method's fluent LINQ chains rooted at a DbSet access
(`_context.Students.Where(...).Include(...).ToList()`), tracking what was included, what was
materialized, and what variable the result was assigned to, and cross-references that against
later navigation-property access in the same method (including `?.` null-conditional chains).

This is heuristic, not semantic analysis:
- **Read-only detection** (for `AsNoTracking`) is "the method never calls `SaveChanges`" — a method
  that reads data for an unrelated write elsewhere won't be flagged, but a getter that happens to
  also fire off an unrelated `SaveChanges` won't be flagged either.
- Cross-method flows aren't tracked — if a query result is passed to another method before its
  navigation is accessed, that access won't be attributed back to the query.
- Navigation detection matches on type name presence in the scanned codebase, not resolved symbols,
  so two unrelated classes that happen to share a name could produce a false positive.

The query-shape and general-throughput detectors are simpler — they match on syntax rather than the
EF model — but carry their own deliberate precision tradeoffs:

- **Sync-over-async** only fires where the receiver is recognisably task-shaped (an invocation
  ending in `Async`, or an identifier containing "task"). `.Result` is a common property name on
  ordinary DTOs, so matching it blindly would bury the real findings in noise.
- **String `+=` in a loop** requires visible evidence the right-hand side is a string (a literal,
  an interpolation, or `.ToString()`), so numeric accumulators aren't swept up.
- **Unbounded query** can't know how big a table is. On a 20-row lookup table it's noise; the point
  is to surface it before that table grows.
- **`async void`** excludes the `(object sender, EventArgs e)` shape, which is the one legitimate use.

In practice this trades perfect precision for the ability to scan anything on disk instantly. Treat
findings as a prioritized worklist, not a verdict — [samples/SampleTarget](samples/SampleTarget)
holds one deliberate instance of all fifteen patterns plus several deliberately-correct methods for
contrast, and can be re-scanned to sanity-check any detector change: a clean run reports exactly
fifteen categories and never flags the correct methods.
