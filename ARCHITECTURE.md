# Piston Architecture

## Overview

Piston is a continuous .NET test runner — an alternative to NCrunch — built around a
**headless controller service** that multiple clients (web UI, IDE extensions, AI agents via MCP) connect
to via a lightweight protocol.

Key constraints:
- No IL rewriting (unlike NCrunch)
- No custom build orchestration — lean on standard `dotnet build` + MSBuild incrementalism
- Leverage Microsoft Test Platform v2 (MTP) for test execution and code coverage
- High parallelism via per-suite process isolation
- Persistent state for coverage maps, impact detection, and failure history
- RDI is NOT a v1 goal

---

## 1. System Architecture

```
+------------------------------------------------------------------+
|                        CLIENT LAYER                               |
|                                                                   |
|  +----------+   +-----------+   +-----------+   +-------------+  |
|  | CLI      |   | VSCode    |   | Web UI    |   | Future IDE  |  |
|  | Client   |   | Extension |   | (browser) |   | Extensions  |  |
|  +----+-----+   +-----+-----+   +-----+-----+  +------+------+  |
|       |               |               |                |          |
+-------|---------------|---------------|----------------|----------+
        |               |               |                |
   named pipe      named pipe      websocket        named pipe
   JSON-RPC        JSON-RPC        JSON-RPC         JSON-RPC
        |               |               |                |
+-------|---------------|---------------|----------------|----------+
|       v               v               v                v          |
|  +----------------------------------------------------------+    |
|  |              PROTOCOL ROUTER                              |    |
|  |  Accepts connections, multiplexes subscriptions,          |    |
|  |  serializes/deserializes JSON-RPC messages                |    |
|  +----------------------------+-----------------------------+    |
|                               |                                   |
|  +----------------------------v-----------------------------+    |
|  |              PISTON ENGINE (headless)                     |    |
|  |                                                           |    |
|  |  +-------------+  +--------------+  +------------------+ |    |
|  |  | File Watcher |  | Impact       |  | Build            | |    |
|  |  | Service      +->| Analyzer     +->| Orchestrator     | |    |
|  |  +-------------+  +--------------+  +--------+---------+ |    |
|  |                                              |            |    |
|  |                                    +---------v----------+ |    |
|  |                                    | Test Orchestrator  | |    |
|  |                                    | (parallel, pooled) | |    |
|  |                                    +---------+----------+ |    |
|  |                                              |            |    |
|  |  +----------------+  +-----------+-----------v----------+ |    |
|  |  | State Store    |  | Coverage  | Result               | |    |
|  |  | (SQLite)       |<-+ Collector | Aggregator           | |    |
|  |  +----------------+  +-----------+----------------------+ |    |
|  +-----------------------------------------------------------+    |
|                                                                   |
|                     CONTROLLER PROCESS                            |
+-------------------------------------------------------------------+
```

The controller is a long-running .NET process. It has no UI. All presentation is
delegated to clients that connect over the protocol.

---

## 2. Project Structure

```
Piston.slnx
  src/
    Piston.Engine/              # Core engine — headless, no UI deps
      Services/                 # File watching, build, test running, TRX/MTP parsing
      Impact/                   # Tiered impact detection (ProjectGraph, coverage map)
      Coverage/                 # Coverage collection + SQLite persistence
      Orchestration/            # Top-level pipeline / state machine
      Models/                   # Engine domain types

    Piston.Protocol/            # Shared contracts — no logic
      Messages/                 # Notifications, commands, activity events
      Dtos/                     # Shared DTOs (TestResult, CoverageData, etc.)
      JsonRpc/                  # JSON-RPC 2.0 framing + source-gen serialization
      Transports/               # Named pipe, WebSocket, stdio transports

    Piston.Hosting/             # Reusable daemon composition (DaemonHost)
      DaemonHost.cs             # Engine + router + web server + MCP wiring
      Protocol/                 # ProtocolRouter, client sessions, dispatchers
      Services/                 # DiagnosticWatcher, McpCallTracker
      Configuration/            # .piston.json loading

    Piston/                     # CLI executable host (namespace Piston.Cli)
      Program.cs                # Entry point — root/daemon/stop/status commands
      DaemonLauncher.cs         # Spawns/attaches background daemon
      BrowserLauncher.cs        # Opens the dashboard in the default browser

    Piston.Desktop/             # Cross-platform desktop app (Photino shell)
      Program.cs                # Window + in-process DaemonHost + solution picker
      WindowsTrayIcon.cs        # Tray icon + close-to-tray (Windows)
      AutostartManager.cs       # Start-on-login registration per OS
      UpdateService.cs          # Velopack auto-update from GitHub Releases

    Piston.Web/                 # Blazor WASM web dashboard (WebSocket client)

    Piston.Mcp/                 # MCP server registration + tool definitions
    Piston.Roslyn/              # Roslyn worker proxy + JSON-RPC contracts
    Piston.Roslyn.Worker/       # Supervised child process hosting MSBuildWorkspace

  extensions/
    vscode/                     # VSCode extension (JSON-RPC over stdio)

  tests/
    Piston.Engine.Tests/
    Piston.Protocol.Tests/
    Piston.Mcp.Tests/
    Piston.Roslyn.Tests/
```

### Dependency Graph

```
Piston (CLI host) ────┐
Piston.Desktop ───────┴─> Piston.Hosting ──> Piston.Engine ──> Piston.Protocol
                                        ├──> Piston.Mcp ─────> Piston.Engine, Piston.Roslyn
                                        └──> Piston.Roslyn
Piston.Web ──────────> Piston.Protocol (via WebSocket JSON-RPC)
Piston.Roslyn.Worker ─> Piston.Roslyn
extensions/vscode ────> Piston.Protocol (via JSON-RPC, not assembly ref)
```

`Piston.Hosting` owns the daemon composition (`DaemonHost`): engine, protocol
router, web server, and optional MCP server. The CLI runs it as a background
process; the desktop app hosts it in-process. The engine remains testable in
isolation and embeddable by future hosts (e.g., a VS extension).

---

## 3. Communication Protocol

### Why JSON-RPC

| Option | Pros | Cons |
|--------|------|------|
| gRPC | Strong typing, streaming, codegen | Heavy dependency, HTTP/2, complex for local IPC |
| JSON-RPC 2.0 | Simple, human-readable, works over any transport | No codegen, manual serialization |
| Custom binary | Fast | Maintenance burden, debugging pain |
| SignalR | WebSocket + fallback, .NET native | Too web-focused, overhead for local IPC |

**Decision: JSON-RPC 2.0** — lightweight, transport-agnostic, debuggable. Works over:
- **Named pipes** (CLI, local IDE extensions) — lowest latency
- **WebSocket** (web UI, remote clients)
- **stdio** (IDE extensions that spawn the controller as a child process)

### Message Categories

**Commands (Client → Engine)** — request/response:

```jsonc
// Start watching a solution
{ "method": "engine/start", "params": { "solutionPath": "C:/repo/App.slnx" } }

// Force a full re-run
{ "method": "engine/forceRun" }

// Set test filter
{ "method": "engine/setFilter", "params": { "filter": "CustomerTests" } }

// Request coverage data for a file
{ "method": "coverage/getForFile", "params": { "filePath": "src/App/Foo.cs" } }

// Get test details
{ "method": "tests/getDetail", "params": { "testFqn": "App.Tests.FooTests.Bar" } }

// Pin/unpin a test
{ "method": "tests/pin", "params": { "testFqn": "...", "pinned": true } }

// Configure engine
{ "method": "engine/configure", "params": { "parallelism": 4, "debounceMs": 300 } }
```

**Notifications (Engine → Client)** — streaming, no response expected:

```jsonc
// Phase changed
{ "method": "engine/phaseChanged", "params": { "phase": "Testing", "detail": "Building 2 projects..." } }

// Test result (streamed per-test as they complete)
{ "method": "tests/result", "params": {
    "fqn": "App.Tests.FooTests.Bar",
    "status": "Passed",
    "duration": 42,
    "suiteName": "App.Tests"
}}

// Batch state snapshot (periodic, for full tree rebuild)
{ "method": "engine/stateSnapshot", "params": {
    "phase": "Watching",
    "suites": [...],
    "passed": 42, "failed": 3, "skipped": 1,
    "lastRunTime": "2026-03-11T14:32:01Z"
}}

// Coverage update for a file (after test run completes)
{ "method": "coverage/fileUpdated", "params": {
    "filePath": "src/App/Foo.cs",
    "lines": [
      { "line": 10, "hits": 5, "status": "covered" },
      { "line": 11, "hits": 0, "status": "uncovered" }
    ]
}}

// Build error
{ "method": "build/error", "params": { "errors": [...] } }
```

### Subscription Model

Clients subscribe to notification streams on connect. The protocol router fans out
notifications to all connected clients. Clients that disconnect and reconnect get
a full state snapshot on reconnection.

---

## 4. Engine Pipeline

### State Machine

```
                    +-----------+
          start()   |           |  stop()
        +---------->|   Idle    |<---------+
        |           |           |          |
        |           +-----+-----+          |
        |                 |                |
        |          startWatching(slnPath)  |
        |                 |                |
        |           +-----v-----+          |
        |           |           |          |
   error/stop      | Watching  |<----+     |
        |           |           |    |     |
        |           +-----+-----+    |     |
        |                 |          |     |
        |          file change       |     |
        |          detected          |     |
        |                 |          |     |
        |           +-----v-----+   |     |
        |           |           |   |     |
        |           | Analyzing |   |     |
        |           | (impact)  |   |     |
        |           +-----+-----+   |     |
        |                 |          |     |
        |          affected projects |     |
        |          identified        |     |
        |                 |          |     |
        |           +-----v-----+   |     |
        |           |           |   |     |
        +-----------|  Building |   |     |
                    |           |   |     |
                    +-----+-----+   |     |
                          |         |     |
                   build succeeded  |     |
                          |         |     |
                    +-----v-----+   |     |
                    |           |   |     |
                    |  Testing  +---+     |
                    |           | done    |
                    +-----+-----+         |
                          |               |
                    build/test failure     |
                          |               |
                    +-----v-----+         |
                    |           +---------+
                    |   Error   | retry/fix
                    |           |
                    +-----------+
```

A new file change during Building or Testing **cancels** the current run and
restarts from Analyzing. This is critical for fast feedback — don't wait for a
stale test run to finish.

### Pipeline Detail

```
File Change Event (debounced)
    |
    v
+-- IMPACT ANALYSIS -----------------------------------------+
|                                                             |
|  Tier 1: File Heuristics (<1ms)                             |
|    - *.cs in test project → run that test project           |
|    - *.cs in src project → find test projects that ref it   |
|                                                             |
|  Tier 2: Project Graph (cached, <100ms after first load)    |
|    - MSBuild ProjectGraph loaded at startup                 |
|    - Changed file → owning project → transitive dependents  |
|    - Filter to only test projects in the dependent set      |
|                                                             |
|  Tier 3: Coverage Map (from previous runs)                  |
|    - Lookup: changed file → which tests covered lines in it |
|    - Intersect with Tier 2 affected test projects           |
|    - Result: specific test FQNs to re-run (not whole suite) |
|                                                             |
|  Output: List of (TestProject, OptionalTestFilter)          |
+-------------------------------------------------------------+
    |
    v
+-- SELECTIVE BUILD -----------------------------------------+
|                                                             |
|  For each affected src project (not test projects):         |
|    dotnet build <project.csproj> --no-restore               |
|                                                             |
|  MSBuild incrementalism handles "nothing to do" fast.       |
|  Build only the projects that changed + their dependents.   |
|  Test projects themselves are built as part of test exec.   |
|                                                             |
|  Parallelism: build independent projects concurrently       |
|  (respect the dependency graph — leaf projects first)       |
+-------------------------------------------------------------+
    |
    v
+-- PARALLEL TEST EXECUTION ---------------------------------+
|                                                             |
|  For each affected test project (concurrently):             |
|    1. Acquire a test host from the process pool             |
|    2. Execute via MTP v2 in-process API:                    |
|       - TestApplication.CreateBuilderAsync(args)            |
|       - Register IDataConsumer for streaming results        |
|       - Register CodeCoverage extension                     |
|       - If Tier 3 provided a filter, apply it               |
|       - RunAsync()                                          |
|    3. Stream results to engine as they arrive               |
|    4. Collect coverage data on completion                   |
|    5. Return test host to pool                              |
|                                                             |
|  Concurrency: configurable (default = CPU count - 1)        |
|  Process pool: pre-warmed, reused across runs               |
+-------------------------------------------------------------+
    |
    v
+-- RESULT AGGREGATION & PERSISTENCE -----------------------+
|                                                             |
|  1. Merge streaming results into engine state               |
|  2. Parse cobertura coverage XML → update CoverageMap       |
|  3. Persist to SQLite:                                      |
|     - Test results (pass/fail/skip, duration, error)        |
|     - Coverage map (file → lines → test FQNs)              |
|     - Run metadata (timestamp, duration, affected projects) |
|  4. Notify all connected clients via protocol               |
+-------------------------------------------------------------+
```

---

## 5. Impact Detection Deep Dive

### Tier 1: File Heuristics

Instant. No external dependencies. Works from first run.

```
Changed: src/App.Domain/Customer.cs
  → Owning project: App.Domain
  → Test projects referencing App.Domain: App.Domain.Tests, App.Integration.Tests
  → Run: App.Domain.Tests, App.Integration.Tests (full suites)
```

### Tier 2: MSBuild ProjectGraph

Loaded once at startup, refreshed on `.csproj`/`.sln` changes.

```csharp
// Uses Microsoft.Build.Graph
var graph = new ProjectGraph(solutionPath);

// On file change, find owning project + transitive dependents
var owningProject = graph.FindProjectContaining(changedFile);
var affectedProjects = graph.GetTransitiveDependents(owningProject);
var affectedTestProjects = affectedProjects.Where(IsTestProject);
```

The graph is cached in memory and on disk (in `.piston/project-graph.json`).
Invalidated when any `.csproj`, `.props`, `.targets`, or `.sln` file changes.

### Tier 3: Coverage-Based Mapping

Available after at least one full test run with coverage enabled. This is where
Piston gets smart over time.

```
SQLite table: coverage_map
+------------------+------+------------------------------+
| file_path        | line | test_fqn                     |
+------------------+------+------------------------------+
| src/Domain/Foo.cs|   42 | Domain.Tests.FooTests.Bar    |
| src/Domain/Foo.cs|   42 | Domain.Tests.FooTests.Baz    |
| src/Domain/Foo.cs|   43 | Domain.Tests.FooTests.Bar    |
+------------------+------+------------------------------+

Query on file change:
  SELECT DISTINCT test_fqn
  FROM coverage_map
  WHERE file_path = 'src/Domain/Foo.cs'
    AND line BETWEEN <changed_line_start> AND <changed_line_end>

Result: Run only FooTests.Bar and FooTests.Baz, not the entire suite.
```

**Line range detection**: Use git diff (or file watcher + content hash) to identify
which lines changed. Map changed lines to covered tests via the coverage map.

**Staleness**: Coverage map entries have a `run_id` column. If the test hasn't been
run in N runs, mark the mapping as stale and fall back to Tier 2 for that test.

### Tier Composition

```
On file change:
  1. Tier 1 instantly determines affected test projects (always runs)
  2. Tier 2 refines to transitive project dependencies (always runs, cached)
  3. IF coverage map has data for the changed files:
       Use Tier 3 to narrow to specific test FQNs
     ELSE:
       Run all tests in affected test projects (Tier 2 result)
  4. Over time, as coverage accumulates, Tier 3 handles more changes
```

This means Piston is **useful immediately** (Tier 1+2) and **gets smarter** as you
use it (Tier 3). No training period required.

---

## 6. Test Execution & Parallelism

### Process Model

```
                     Piston Controller Process
                              |
              +---------------+---------------+
              |               |               |
        +-----v----+   +-----v----+   +------v---+
        | Test Host |   | Test Host |   | Test Host|
        | Process 1 |   | Process 2 |   | Process 3|
        | (Suite A) |   | (Suite B) |   | (Suite C)|
        +-----------+   +-----------+   +----------+

        Each test host runs one test assembly at a time.
        Pool size = configurable (default: CPU count - 1)
```

### Per-Suite vs Per-Test Isolation

| Model | Isolation | Overhead | Parallelism | Use Case |
|-------|-----------|----------|-------------|----------|
| Per-suite (assembly) | High | Low (1 process per assembly) | Good | Default — simple, reliable |
| Per-test | Maximum | High (1 process per test) | Maximum | Flaky tests, resource conflicts |
| Per-class | Medium | Medium | Good | Compromise |

**Decision: Per-suite by default**, with per-test opt-in via configuration or attribute.

Per-suite is the sweet spot because:
- MTP v2 already handles per-test parallelism WITHIN a process
- Process startup cost (~200-500ms) amortized across many tests
- Most test isolation issues are cross-assembly, not intra-assembly
- Users can opt into per-test for specific suites that need it

### Process Pool

```csharp
// Pool maintains warm test host processes
// A test host = a dotnet process ready to load a test assembly
// On run: acquire host → load assembly → execute → return to pool

class TestHostPool
{
    int MaxHosts { get; }         // default: Environment.ProcessorCount - 1
    Queue<TestHost> Available;
    HashSet<TestHost> Active;

    async Task<TestHost> AcquireAsync(CancellationToken ct);
    void Release(TestHost host);
    void DrainAndDispose();       // shutdown
}
```

Hosts are recycled after N runs (configurable) to prevent memory bloat from
test-leaked state. If a host crashes, it's discarded and a new one is spawned.

### Execution Flow Per Suite

```
1. AcquireAsync(ct) → get a TestHost from the pool
2. host.LoadAssembly(testProject.OutputDll)
3. host.Execute(filter: optionalTestFqns, coverage: true)
     → Streams TestNodeUpdateMessage back via IDataConsumer
     → Engine receives per-test results in real-time
     → Engine broadcasts to clients
4. host.CollectCoverage() → cobertura XML
5. pool.Release(host)
```

### Cancellation

When a new file change arrives during testing:
1. Cancel all active test hosts (cooperative cancellation via CancellationToken)
2. Hosts that are mid-test gracefully stop (MTP supports cancellation)
3. Partial results are kept — tests that completed are valid
4. Re-enter Analyzing phase with the new change

This ensures the user always sees results from the most recent code, not stale runs.

---

## 7. Coverage Integration

### Collection

MTP v2 provides coverage via `Microsoft.Testing.Extensions.CodeCoverage`.

```csharp
// During test host setup:
builder.AddCodeCoverageProvider();
// OR via CLI args to the test host:
// --coverage --coverage-output-format cobertura
```

After each test suite completes, the coverage data is emitted as a cobertura XML
file. The engine parses this and updates the coverage map.

### Storage Schema (SQLite)

```sql
-- Per-line coverage from the most recent run
CREATE TABLE coverage_map (
    file_path   TEXT NOT NULL,
    line_number INTEGER NOT NULL,
    test_fqn    TEXT NOT NULL,
    hit_count   INTEGER NOT NULL DEFAULT 1,
    run_id      INTEGER NOT NULL,
    PRIMARY KEY (file_path, line_number, test_fqn)
);

CREATE INDEX idx_coverage_file ON coverage_map(file_path);
CREATE INDEX idx_coverage_test ON coverage_map(test_fqn);

-- Summary per file (for fast "is this file covered?" queries)
CREATE TABLE coverage_summary (
    file_path       TEXT PRIMARY KEY,
    total_lines     INTEGER NOT NULL,
    covered_lines   INTEGER NOT NULL,
    coverage_pct    REAL NOT NULL,
    last_updated    TEXT NOT NULL  -- ISO 8601
);
```

### Querying

For IDE extensions showing inline coverage markers:

```sql
-- Get coverage for a file (for gutter markers)
SELECT line_number, SUM(hit_count) as total_hits
FROM coverage_map
WHERE file_path = ?
GROUP BY line_number;

-- Get which tests cover a specific line (for "go to covering tests")
SELECT test_fqn, hit_count
FROM coverage_map
WHERE file_path = ? AND line_number = ?;
```

For impact detection (Tier 3):

```sql
-- Which tests to re-run when lines 10-25 of Foo.cs changed?
SELECT DISTINCT test_fqn
FROM coverage_map
WHERE file_path = 'src/Domain/Foo.cs'
  AND line_number BETWEEN 10 AND 25;
```

### Coverage Freshness

Coverage data becomes stale when code changes. Strategy:

1. On file change, mark coverage entries for that file as `stale` (don't delete)
2. After the next test run, replace stale entries with fresh data
3. Stale coverage is still shown to the user (dimmed) but not used for Tier 3
   impact detection — fall back to Tier 2 for stale files

---

## 8. State Persistence

### Location

```
<solution-root>/
  .piston/
    piston.db           # SQLite database
    project-graph.json  # Cached MSBuild project graph
    config.json         # User configuration (overrides)
```

`.piston/` is gitignored (machine-local state).

### SQLite Schema

```sql
-- Test results history
CREATE TABLE test_results (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id      INTEGER NOT NULL,
    test_fqn    TEXT NOT NULL,
    display_name TEXT,
    status      TEXT NOT NULL,  -- Passed/Failed/Skipped
    duration_ms INTEGER,
    error_msg   TEXT,
    stack_trace TEXT,
    suite_name  TEXT NOT NULL,
    timestamp   TEXT NOT NULL
);

CREATE INDEX idx_results_fqn ON test_results(test_fqn);
CREATE INDEX idx_results_run ON test_results(run_id);

-- Run metadata
CREATE TABLE runs (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp       TEXT NOT NULL,
    trigger         TEXT NOT NULL,  -- FileChange/Manual/Startup
    changed_files   TEXT,           -- JSON array
    affected_projects TEXT,         -- JSON array
    build_duration_ms INTEGER,
    test_duration_ms  INTEGER,
    total_passed    INTEGER,
    total_failed    INTEGER,
    total_skipped   INTEGER
);

-- Coverage map (see Section 7)

-- Project graph cache
CREATE TABLE project_graph (
    project_path    TEXT PRIMARY KEY,
    is_test_project INTEGER NOT NULL,
    references      TEXT NOT NULL,  -- JSON array of project paths
    source_files    TEXT NOT NULL,  -- JSON array of source file paths
    last_modified   TEXT NOT NULL
);

-- User preferences (pinned tests, filters, etc.)
CREATE TABLE preferences (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
```

### Why SQLite

- Zero configuration, single file, embedded
- Handles concurrent reads well (WAL mode)
- Fast indexed queries for coverage lookups
- Survives process crashes (ACID)
- Easy to inspect/debug (`sqlite3 .piston/piston.db`)
- Ships with .NET via `Microsoft.Data.Sqlite`

### Cache Invalidation

| Cached Data | Invalidation Trigger |
|-------------|---------------------|
| Project graph | Any `.csproj`, `.props`, `.targets`, `.sln` file change |
| Coverage map | Source file changes mark entries stale; rebuilt on next run |
| Test results | Never invalidated — append-only history |
| Preferences | Manual user action only |

---

## 9. Build Strategy

### Principles

1. Use `dotnet build` — don't reinvent MSBuild
2. Build only what changed (project-level targeting)
3. Leverage MSBuild incrementalism (`--no-restore` after first run)
4. Parallel builds for independent projects

### Flow

```
Affected projects (from Impact Analyzer):
  [App.Domain, App.Services]   (src projects that changed)
  [App.Domain.Tests, App.Integration.Tests]  (test projects to run)

Build plan:
  1. dotnet build App.Domain.csproj --no-restore
  2. dotnet build App.Services.csproj --no-restore  (parallel with 1 if independent)
  3. Test projects are built by MTP when loading assemblies
     (or: dotnet build App.Domain.Tests.csproj --no-restore --no-dependencies
      if we need the DLL before MTP)
```

### Avoiding NCrunch's Build Complexity

NCrunch replicates the entire solution into shadow workspaces and runs custom
build orchestration. This is fragile and causes issues with:
- NuGet package resolution
- Build props/targets inheritance
- Source generators
- Analyzers

Piston avoids ALL of this by building in-place with standard `dotnet build`.
MSBuild already handles incrementalism well — if nothing changed, it's a no-op
(~100ms). The only cost is that builds are serialized (MSBuild lock on project
assets), but this is acceptable because:
- Most changes affect 1-3 projects
- MSBuild incremental build for "nothing changed" is ~100ms
- We only build what the impact analyzer says changed

---

## 10. Configuration

### Hierarchy (highest priority first)

1. Client-sent commands (runtime overrides)
2. `.piston/config.json` (solution-level)
3. `~/.piston/config.json` (user-level global)
4. Built-in defaults

### Config Schema

```jsonc
{
  // Engine
  "debounceMs": 300,              // File change debounce
  "parallelism": 0,               // 0 = auto (CPU count - 1)
  "processPoolSize": 0,           // 0 = auto (same as parallelism)
  "processRecycleAfter": 50,      // Recycle test host after N runs

  // Impact detection
  "impactDetection": {
    "tier1": true,                 // File heuristics (always fast)
    "tier2": true,                 // Project graph
    "tier3": true,                 // Coverage-based (when available)
    "fallbackToFullRun": false     // If tiers disagree, run everything?
  },

  // Coverage
  "coverage": {
    "enabled": true,               // Collect coverage on every run
    "format": "cobertura"          // Coverage output format
  },

  // Build
  "build": {
    "noRestore": true,             // Skip restore (faster after first run)
    "additionalArgs": []           // Extra args to dotnet build
  },

  // Test
  "test": {
    "defaultFilter": null,         // Default test filter expression
    "timeout": 300000,             // Per-suite timeout (ms)
    "perTestIsolation": false      // Per-test process isolation (slow but safe)
  },

  // Protocol
  "protocol": {
    "namedPipe": "piston-{solutionHash}",  // Named pipe name
    "webSocket": null,             // WebSocket port (null = disabled)
    "webSocketHost": "127.0.0.1"   // Bind address for WebSocket
  }
}
```

---

## 11. Client Architecture (Web UI)

The primary client is a Blazor WebAssembly dashboard served by the daemon itself
and connected over WebSocket. It:
1. Connects to the daemon's `/ws` endpoint (with automatic reconnection/backoff)
2. Subscribes to state notifications
3. Renders live views from state snapshots and streaming test results
   (tests, diagnostics, activity feed, MCP call history)
4. Sends commands (force run, filter, clear) via JSON-RPC

```
Browser (Blazor WASM)               Daemon Process
+-----------+                       +------------------+
|           |  -- engine/start -->  |                  |
|  Render   |  <-- stateSnapshot -- |  Engine running  |
|  Loop     |  <-- tests/result --  |  File watching   |
|           |  <-- tests/result --  |  Building...     |
|  UI       |  <-- phaseChanged --  |  Testing...      |
|  Events   |  -- engine/forceRun ->|                  |
|           |  <-- stateSnapshot -- |  Results ready   |
+-----------+                       +------------------+
```

### Startup Modes

1. **`piston`** (no args): Auto-starts the daemon if needed, opens the web dashboard
2. **`piston daemon`**: Runs the daemon in the foreground (pipe + web + optional MCP)
3. **`piston daemon --stdio`**: stdio JSON-RPC transport for IDE extensions
4. **`piston stop` / `piston status`**: Manage a running daemon over the named pipe

> Historical note: the original client was a TUI; it was replaced by the
> Blazor WASM web UI.

---

## 12. Future: VSCode Extension

NCrunch doesn't support VSCode — this is Piston's differentiator.

The extension would:
1. Spawn `piston daemon --stdio` as a child process (stdio transport)
2. Communicate via JSON-RPC over stdio
3. Show inline coverage markers (gutter decorations)
4. Show test status in the test explorer
5. Show failure details in the problems panel
6. Support "run affected tests" on file save

The protocol is designed to support this use case natively — all the data
(coverage per line, test results, phase changes) flows as notifications.

---

## 13. Failure Handling & Resilience

### Controller Crash Recovery

1. On startup, check `.piston/piston.db` for state from last session
2. Load cached project graph
3. Load coverage map (still valid if code hasn't changed much)
4. Start watching — first file change triggers a fresh run
5. Coverage map self-heals as new runs produce fresh data

### Test Host Crash

1. Pool detects host process exit (non-zero exit code or timeout)
2. Marks that suite's results as `Error`
3. Spawns replacement host
4. Optionally retries the suite (configurable)

### Build Failure

1. Parse MSBuild error output (existing logic)
2. Set phase to `Error`
3. Broadcast build errors to clients
4. On next file change, retry build
5. Track "last successful build" for partial fallback

---

## 14. Performance Budget

Target latencies for a 100-project solution with 5,000 tests:

| Operation | Target | Notes |
|-----------|--------|-------|
| File change → impact analysis complete | <200ms | Tier 1+2, cached graph |
| Selective build (1-3 projects, incremental) | <2s | MSBuild incremental |
| Test execution start (first result streaming) | <1s | Pre-warmed process pool |
| Full test suite (5,000 tests, 8 cores) | <30s | Parallel per-suite |
| Coverage map update | <500ms | SQLite batch insert |
| Client state notification | <50ms | JSON-RPC over named pipe |
| Cold start (load cached state) | <2s | SQLite + project graph |

---

## 15. Roslyn Workspace & MCP Server

### Architecture Overview

```
Piston Controller
  ├── Protocol Router (named pipe / stdio / WebSocket JSON-RPC) ─── CLI / IDE / web clients
  ├── MCP Server (HTTP/SSE on --mcp-port) ─── AI agents
  │
  ├── Piston Engine (test runner, file watcher, build)
  │
  └── Roslyn Worker (supervised child process)
        └── MSBuildWorkspace + SemanticModel + Renamer
```

### New Projects

| Project | Purpose |
|---------|---------|
| `Piston.Roslyn` | Shared DTOs, JSON-RPC contracts, and proxy for the Roslyn worker process. No Roslyn dependency — safe to reference from the controller. |
| `Piston.Roslyn.Worker` | Standalone executable that hosts an `MSBuildWorkspace`. Runs as a supervised child process to isolate Roslyn/MSBuild from the main controller. |
| `Piston.Mcp` | MCP server registration and tool definitions. Exposes Piston engine + Roslyn workspace as MCP tools for AI agents. |

### Updated Dependency Graph

```
Piston (CLI host) ───> Piston.Engine ───> Piston.Protocol
                  ├──> Piston.Roslyn
                  └──> Piston.Mcp ──────> Piston.Engine
                                     └──> Piston.Roslyn
Piston.Roslyn.Worker ─> Piston.Roslyn ──> Piston.Protocol
```

### MCP Tool Catalog

The MCP server exposes 10 tools across two categories:

**Roslyn Tools** (code intelligence via the supervised worker):

| Tool | Description |
|------|-------------|
| `LoadWorkspace` | Load a `.sln` or `.csproj` workspace for code analysis. Must be called first. |
| `GetDiagnostics` | Get compiler diagnostics (errors/warnings) for a project or the entire workspace. |
| `SemanticSearch` | Find all references to a symbol by name using Roslyn semantic analysis. |
| `GetAst` | Get a pruned AST for a file showing declarations (classes, methods, properties). |
| `Rename` | Rename a symbol at a specific location. Supports preview mode. |
| `NotifyFileChanged` | Notify the workspace that a file changed externally so it updates its in-memory state. |

**Test Tools** (engine operations):

| Tool | Description |
|------|-------------|
| `RunTests` | Force a full test run and return results summary. |
| `GetTestResults` | Get current test results from the last run. |
| `SetTestFilter` | Set a test filter to narrow which tests are run. |
| `ClearResults` | Clear all test results and coverage data. |

### Supervised Child Process Model

The Roslyn worker runs as a separate `dotnet` process (`Piston.Roslyn.Worker`) to
provide crash isolation. MSBuild workspace loading can fail or corrupt state in
ways that would take down the controller — the child process model prevents this.

Key design decisions:
- **Lazy initialization**: The worker process is not spawned until a Roslyn tool is
  first invoked. This avoids startup cost when MCP/Roslyn features are not used.
- **Crash recovery**: If the worker crashes, the proxy detects the broken pipe and
  respawns a fresh worker on the next request.
- **Communication**: JSON-RPC over stdin/stdout pipes to the child process.
- **Lifecycle**: The worker is terminated when the controller shuts down.

### Workspace Synchronization

The Roslyn workspace stays in sync with file changes through two mechanisms:

1. **Explicit notification**: The `NotifyFileChanged` MCP tool allows AI agents to
   tell the workspace about files they modified.
2. **Refactor-then-retest flow**: When an agent uses `Rename` (applied mode), the
   engine's file watcher detects the changed files, triggers a rebuild, and re-runs
   affected tests automatically. The flow is:
   ```
   Agent calls Rename → files written to disk → FileWatcher detects changes
     → Engine rebuilds affected projects → Engine re-runs affected tests
     → Agent calls GetTestResults to verify
   ```

### Configuration

MCP can be enabled via CLI or config file:

```bash
# CLI
piston daemon --mcp-port 3001

# .piston.json
{ "mcpPort": 3001 }
```

When `mcpPort` is set in `.piston.json`, the daemon automatically starts the MCP
server alongside the named pipe listener. CLI `--mcp-port` takes precedence.

---

## 16. Migration Path from Current Architecture (historical)

All phases below are complete. The TUI client referenced in Phases 1 and 5 was
later replaced by the Blazor WASM web UI (section 11).

**Phase 1**: Extract engine from TUI
- Move orchestration logic from `Piston.Core` → `Piston.Engine`
- Define protocol contracts in `Piston.Protocol`
- TUI becomes a thin client consuming protocol messages

**Phase 2**: Add impact detection
- Integrate MSBuild ProjectGraph (Tier 2)
- Replace "rebuild everything" with selective build
- Add project-level test filtering

**Phase 3**: Add coverage integration
- Enable MTP v2 code coverage collection
- Parse cobertura XML into SQLite coverage map
- Implement Tier 3 impact detection

**Phase 4**: Add process pool
- Replace single `dotnet test` invocation with parallel test hosts
- Implement process pooling and recycling

**Phase 5**: Headless mode + multi-client
- Extract controller from TUI process
- Implement named pipe transport
- Support `--headless` and `--connect` modes

**Phase 6**: VSCode extension
- TypeScript extension using JSON-RPC over stdio
- Inline coverage markers
- Test explorer integration
