# Piston Architecture

## Scope and status

Piston is an MCP-based continuous test runner for .NET. Agents reach it through a
per-session stdio MCP server (`piston mcp`) or the daemon's HTTP MCP endpoint;
people use the same engine through the web dashboard. It builds and tests in the
watched checkout using standard `dotnet` commands, not shadow workspaces or IL
rewriting.

This document describes implemented behavior and its limits. Planned work is in
[docs/ROADMAP.md](docs/ROADMAP.md).

## Components

| Project | Responsibility |
|---|---|
| `Piston` | .NET tool: CLI launch/attach, browser, foreground daemon, stop/status, stdio MCP server (`piston mcp`); daemon composition, protocol routing, web hosting, configuration, MCP tools and call tracking. |
| `Piston.Engine` | Watcher, impact analysis, builds, test execution, current state, coverage processing. |
| `Piston.Protocol` | JSON-RPC contracts, DTOs, framing, named-pipe and stdio transports. |
| `Piston.Web` | Blazor WebAssembly dashboard connected over WebSocket. |
| `Piston.LiveCollector` | VSTest data collector, deployed with the engine, that streams per-test start/end events to the engine over loopback (`PISTON_LIVE_PORT`). |
| `Piston.ProcessHost` | Small launcher that puts a test process in its own Unix session or Windows job, so the engine can terminate the test process and its descendants together (`OwnedTestProcess`). |

Hosting and MCP source live in `src/Piston/Hosting` and `src/Piston/Mcp`.
Their `Piston.Hosting` and `Piston.Mcp` namespaces remain, but they are not
separate projects or assemblies.

The production dependency shape is:

```text
Piston (CLI + host + MCP) ------> Piston.Engine ------> Piston.ProcessHost
         |                                |
         |                                +---- deploys Piston.LiveCollector (not loaded in-process)
         |
         +---------------------> Piston.Protocol <----- Piston.Web
         |
         +---- publishes/copies Piston.Web static assets (not an assembly reference)
```

`Piston.Engine` remains the MSBuild-loading and test-execution boundary and has
no reference to `Piston.Protocol`. Its typed `EngineActivityEvent` records carry
engine activity without JSON payloads. The host's `DtoMapper` converts them to
the existing wire `ActivityEvent` and data DTOs, preserving discriminators,
timestamps, camel-case fields, null omission, and protocol source generation.
MCP call-log data remains a host/protocol concern.

`Piston.Tests` combines tool/hosting, protocol, and MCP tests.
`Piston.Engine.Tests` retains `Piston.TestProcessHost` and
`Piston.ExecutionLeaseHolder` for process-tree cleanup and cross-process lease
coverage. `tests/fixtures/` holds the small solution the opt-in dogfood and stdio
MCP smoke tests copy. `samples/` holds one sample project per test framework,
outside `Piston.slnx`, for the packed-tool smoke tests that `build.cs` runs.

The CLI runs the daemon in a separate process; `piston daemon` hosts `DaemonHost`
in that process. `piston mcp` (`McpStdioHost`) instead runs an in-process engine
for one agent session, selecting the worktree from its working directory and
holding that worktree's ownership lock until the session ends.

## Transports and clients

The daemon routes JSON-RPC requests and notifications over named pipes, stdio,
and WebSocket. The browser uses `/ws`. HTTP MCP is a separate, always-on agent
endpoint on port 5200, overridable with `--mcp-port` or `mcpPort` in `.piston.json`.
Its URL is the root, `http://localhost:5200`, not `/mcp`.

Implemented protocol operations include stop, force run, set filter, clear
results, state retrieval, and MCP call-log retrieval. `engine/start` is rejected
by the headless dispatcher because the solution is configured at launch.
Coverage requests return the solution's coverage summary, one file's line
coverage (optionally with its source), and LCOV or Cobertura exports.
`coverage/getCoveringTests` returns the tests the impact map records as executing
a file, with their evidence granularity and executed line ranges.
`coverage/getChangedLines` returns the verdicts for the changed lines of the git
changes (`base`, `baseRef`), as `get_changed_line_coverage` does (see
[MCP behavior and limitations](#mcp-behavior-and-limitations)): per file, line
ranges per verdict, unknown ranges per reason, partial-branch lines and up to 20
failing tests. File detail is bounded to half the message size limit: files
past it give only their totals (`detailOmitted`), then are counted in
`filesOmitted`, with a warning. The overall totals always count every changed line.
`source/get` returns a source file's lines for the dashboard's stack-trace view;
it refuses paths that resolve (following symlinks) outside the solution
directory, files over 1 MB and binary files.
Clients receive state, test progress, build errors, and activity notifications.
`machine/getOverview` reads machine-wide worktree owners, budget, live leases and
queue tickets for the dashboard. The dashboard presents current test/build state,
run history and comparison, aggregate coverage with covering tests, activity,
MCP call history and a Machine tab for the workspace overview.

See [README.md](README.md) for executable commands and the actual configuration
shape. The daemon's `--stdio` JSON-RPC transport is not MCP; the stdio MCP server
is the separate `piston mcp` command.

## Engine pipeline

```text
Startup or manual run ------> Full-run analysis
Debounced file changes -----> Impact analysis
                                     |
                                     v
                               dotnet build
                                     |
                              successful build
                                     |
                                     v
                         bounded dotnet test processes
                                     |
                                     v
                       merge results / process coverage
                                     |
                                     v
                              Watching phase
```

Startup initializes the solution graph and, when coverage is enabled, the SQLite
coverage store, starts watching, and triggers an initial build/test run. The
pipeline awaits graph initialization before choosing per-project test targets.

The watcher batches changes to every source-fingerprint input: any file under the
solution directory except build outputs and directories such as `.git` and
`node_modules`, plus the nearest ancestor `global.json`, `Directory.Build.*`,
`Directory.Packages.props` and `NuGet.config`. Changes to `.sln`, `.slnx`, `.slnf`,
`.csproj`, `.props` or `.targets` refresh the solution graph; solution, `.props`
and `.targets` changes trigger a full run.

A new trigger cancels the preceding pipeline, and a semaphore serializes runs.
Each run then waits for machine-wide admission (see
[Worktree ownership and machine execution budget](#worktree-ownership-and-machine-execution-budget))
before building.
Build/test subprocesses are terminated on cancellation. This is not a guarantee
that partial or previously displayed results reflect the newest source. Selective
runs retain results from untouched suites, so consumers must distinguish current
run results from preserved state.

Build failure prevents test execution and puts the engine in `Error`. Successful
pipeline completion returns to `Watching`, including when tests fail; individual
test statuses and runner errors must be inspected as well as the engine phase.

## Impact analysis

### Project-level selection (implemented)

`ImpactAnalyzer` first uses directory walking to find an owning `.csproj`, with
the in-memory MSBuild solution graph as another ownership lookup. A test-project
source change selects that test project. A production-project change follows
transitive dependents and selects affected test projects.

Unknown ownership, unavailable graph data, and broad build-input changes can
fall back to a full run. Project/build-input changes can require graph
reinitialization. The graph is held in memory; there is no implemented
`.piston/project-graph.json` cache or persisted graph recovery.

### Changed-line selection (implemented)

With coverage enabled, Tier 3 selects tests from the **impact map**
(`IImpactMapStore`, implemented by `SqliteCoverageStore`). Each recorded *unit*
belongs to a test project, has a granularity (`run`, `class` or `test`) and lists
the tests it measured and, per source file, the line spans those tests executed
plus a content-addressed snapshot of the file as measured.

- **Recording.** After a coverage run, the orchestrator records one `run` unit per
  test project from that project's reports. A full run in which all the project's
  tests ran cleanly replaces the unit; other runs merge it by C# member identity.
  Each executed member is keyed by its containing namespace/type, signature and
  token hash. Earlier spans are carried forward only through unchanged members;
  evidence that cannot be validated becomes file-level rather than being shifted
  by an unvalidated line diff.
- **Backfill.** `run` evidence credits every executed line to every test of the
  project. When the daemon is idle, `ImpactBackfill` plans class (or test) units
  that are missing or outdated, and the orchestrator runs each with a name filter
  and coverage, recording a `class`/`test` unit. A unit is skipped when not every
  planned test ran. Any new run cancels the backfill; progress is published as
  `ImpactBackfill` in the state snapshot.
- **Selection.** `SelectTests` compares member keys and body hashes against each
  unit's snapshot. A changed or removed executed member selects its tests and
  includes the member identity in its `TestSelectionReason`; edits outside
  recorded members conservatively select every test that executed the file.
  Current finer evidence about a test overrides coarser evidence. A line insertion
  that leaves member identities and bodies unchanged remaps its spans without
  dirtying the class/test units. Analyzing a change dirties units when their
  executed member or non-member syntax changes, or declarations are added or
  removed: even unchanged callers can bind differently after an overload is added.
- **Abstaining.** A changed test file, a changed file with no recording, or more
  than `ImpactAnalysisResult.MaxFilteredTests` (100) selected tests falls back to
  project selection, and the run records why (`SelectionExplanation`). Without an
  impact-map hit, the older file-level path (`coverage_set_*`) may still narrow by
  whole files.

This is a heuristic: code reached by reflection, static state or data files can
affect a test without appearing in its spans, and a full or forced run remains
authoritative. Selective runs filter by method, so a merge that completed without
a crash or runner error also removes recorded cases of the methods it ran that no
longer exist (theory display names that change between runs).

## Builds and execution

Builds use standard `dotnet build`. Full runs target the original solution and
allow restore. The selective build target set is the union of affected
non-test projects and affected test projects. A test-only edit targets that test
project, rather than accidentally building the whole solution.

For solution-member targets, Piston issues one
`dotnet build <temporary-filter>.slnf`, with restore enabled. The filter is written
outside the watched tree and removed afterwards. MSBuild handles parallel
scheduling and ProjectReferences; Piston does not launch independent concurrent
build commands. This refreshes referenced libraries in the affected test outputs
before `--no-build` test execution, while retaining the real solution's
`SolutionDir` and `SolutionPath`.

The filter uses the user's `.sln` or `.slnx` as its base. For a user `.slnf`, it
resolves the underlying solution and respects membership in the user's filter.
Non-member targets reached through ProjectReferences, or an unreadable/unsupported
solution path, fall back to sequential per-project
`dotnet build <project> --no-restore`. This fallback is not a restore guarantee.

A selective run with no affected test projects builds only and skips tests.
Startup/manual runs and full-run fallbacks use the original solution, then all
test projects available from the graph. Selective result merging uses
test FQN rather than timestamp-dependent TRX suite names, avoiding stale
duplicates from that merge path.

Both execution backends start a new process per test project and run (unless
warm hosts are enabled for MTP), each under `Piston.ProcessHost`:

| Backend | Current mechanism |
|---|---|
| VSTest | `dotnet test` on the project/solution target, `--no-build`, normal verbosity, TRX results. The `PistonLive` data collector streams per-test start/end events; stdout is parsed for progress as well. |
| Microsoft.Testing.Platform (MTP) | Runs the built test app in MTP server mode (JSON-RPC), which reports each test as it starts and finishes. Falls back to `dotnet test --project <csproj> --no-build` with stdout parsing when the app is not found or server mode fails. Coverage runs stay in server mode: the coverage extension's test host controller connects first as a passive node (`multipleConnectionProvider`) and the test host it relaunches connects next and serves the run. The fallback needs the MTP runner configured in the solution's `global.json`. |

See the [MTP server-mode investigation](docs/mtp-server-mode.md) for identity and coverage findings.

When the graph identifies an MTP output path, the composite strategy selects
MTP; otherwise it selects VSTest. `Auto` and the currently named `Process` mode
both use this routing. `InProcess` is accepted as a configuration enum value but
throws `NotSupportedException` during engine construction. There is no implemented
in-process MTP execution mode.

With `warmTestHosts`, `MtpWarmHostPool` keeps one MTP server-mode host per
test app alive: `MtpServerSession.Connection` initializes once and then serves
any number of `testing/discoverTests` and `testing/runTests` requests, one at a
time. The pool replaces a host when a fingerprint of its output directory (file
names, sizes and write times) changes, when it has exited, and after a failed,
timed-out or cancelled request. A request for a busy host, and a retry after a
reused host fails before reporting a test, run in a cold process. Coverage runs
use `dotnet test` and never a warm host. The engine disposes the pool on
shutdown, sending `exit` before killing.

`TestProcessPool` is a semaphore-based concurrency limiter, not a pool of
prewarmed, reusable processes. Automatic concurrency is
`Math.Max(1, Environment.ProcessorCount / 2)`. `processRecycleAfter` logs a debug
warning after a configured number of runs; it does not recycle persistent hosts.
There is no per-test or per-class process-isolation option. Test-framework
parallelism inside each process is separate from Piston's project concurrency.

Slow tests are timed from live progress: a test is timed from the first snapshot
that reports it `Running`. The orchestrator checks running tests every second,
publishes the longest one in the state snapshot (`LongestRunningTest`) and, past
`hangThreshold`, flags it as possibly hanging, logs it once and records it in the
run's `PossiblyHangingTests`. With `testTimeout` set,
`TimeoutTestExecutionStrategy` wraps the execution strategy for each project: past
the limit it cancels a linked token for that project only. The runners kill the
process and return an empty result, and the decorator rebuilds the result from the
last live snapshot: the slow test `Failed` with a timed-out message, other
`Running` tests `NotRun`. The project's outcome is `TimedOut`, which makes the run
`Incomplete`.

## Worktree ownership and machine execution budget

`src/Piston.Engine/Execution` coordinates daemon processes of one OS user through
OS file locks in a per-user directory (`ExecutionCoordinationDirectory`):
`$PISTON_EXECUTION_DIRECTORY` (absolute), or `Piston/execution` under
LocalApplicationData. State lives in `v1/` (`owners/`, `queue/`, `leases/`,
`admission.lock`); the operator-editable `budget.json` sits beside it. On Unix,
.NET implements `FileShare.None` with `flock(LOCK_EX|LOCK_NB)` per open file
description. On Windows it uses share modes. Either way the OS drops the lock
when the process exits or is killed, which is how stale owners are recovered. No
PID liveness checks or heartbeats are involved. `EnsureReady` creates the
directories, tightens existing Unix directories to mode 0700, and opens a probe file twice. If the second open
does not conflict (`DOTNET_SYSTEM_IO_DISABLEFILELOCKING`), it throws instead of
continuing without enforcement.

Update `budget.json` with atomic replacement: write complete JSON to a temporary
file in the same directory, then rename it over the existing file without deleting
the old file first. `ExecutionBudget.Load` retries JSON parse failures (including
empty/partial writes) and I/O failures five times with 20 ms waits, adding at most
100 ms of delay per load. Persistent failures remain explicit invalid-budget
errors; schema errors and access denial fail immediately.

**Ownership** is separate from the orchestrator's per-daemon run semaphore.
`PistonEngine.StartAsync` validates the budget, then takes
`WorktreeOwnership`. That is an exclusive lock on
`owners/<sha256(canonical worktree)>.lock`, plus a JSON sidecar with PID, solution,
worktree, acquisition time, host kind and (for daemons) local dashboard URL used in
conflict messages and the machine overview. Dashboard URLs are accepted only when
they use HTTP on localhost or a loopback address with no path, query or credentials.
The canonical worktree is
the solution path with symlinks resolved, walked up to the first `.git` directory
or file, including solution-file symlinks. Without one, it is the solution directory.
Windows paths are case-folded; macOS uses `F_GETPATH` to preserve actual directory
spelling on both case-sensitive and case-insensitive volumes. A conflict raises `WorktreeOwnershipConflictException`. The CLI
prints it and exits 1, and the launcher reports the daemon's early exit instead of
waiting for its pipe. Engine disposal cancels and drains every tracked pipeline before
disposing shared services and releasing ownership. Both `DisposeAsync` and synchronous
`Dispose` wait for this drain. The OS releases ownership on crash.
Lock files are never deleted, so a deleted-and-recreated file can never let two
holders coexist.

**Admission** (`MachineExecutionCoordinator`, behind the internal
`IExecutionAdmission`) runs inside `TriggerRunAsync`, after the per-daemon run lock
and before the build. Each run's reservation is the daemon's effective
`TestProcessPool` size in CPU slots plus optional `execution.memoryMb`. Under the
global `admission.lock` the coordinator:

1. creates a ticket `queue/<seq>_<cpu>_<mem>_<pid>_<runId>.ticket`, held open
   exclusively. `seq` is UTC ticks, made monotonic past the highest existing entry.
2. on each 100 ms poll, re-reads and validates `budget.json`.
3. deletes entries whose lock is no longer held (crashed processes).
4. sums held leases and admits strictly FIFO: only the head ticket, and only if it
   fits. Larger runs are never starved by smaller ones behind them.

On admission it creates `leases/<…>.lease` and releases the ticket. Queue status
changes are published as the active `TestRunRecord` with `Status = Queued` and a
`TestRunAdmission`. The admitted status is kept on the record and mapped to
`TestRunAdmissionDto`, MCP `get_test_run` JSON and `get_test_results` text.
Windows entries are explicitly closed and deleted under the admission lock rather
than marked delete-pending while held. This keeps sharing violations distinct from
permission failures; access denial is an explicit coordination error, not a retry loop.

The lease is disposed in the pipeline's `finally` (completion, cancellation, fault)
before the run lock is released. Cancellation while queued removes the ticket.
When a newer trigger supersedes a queued run, the replacement reuses its sequence
and keeps its place. Stop/dispose does not. Admission errors (invalid budget,
unrecognised held entries, IO) fault the run with an explicit reason. Only a missing
budget file selects the default; unreadable files do not. Build/test cancellation
waits for the killed runner to exit before the pipeline releases its lease.

`MachineExecutionOverviewReader` takes `admission.lock` briefly and reads the
same live ticket and lease names without cleaning up stale files. It reports CPU
and memory use from held leases, FIFO queue positions, and owner-to-entry matches
by PID. The dashboard polls `machine/getOverview` only while the Machine tab is
open; a 500 ms lock timeout or invalid budget is returned as visible overview
error text rather than silently inventing resource values. A failed coordination
scan discards partial entries and marks availability false; usage and counts are
unknown, and live owners are not labelled idle. `piston status --all`
uses the same reader.
Ownership scan availability is tracked separately: unreadable owner directories
or locks produce a visible error and unknown ownership, not an empty-success list.
Filesystem probes distinguish missing directories from access and I/O failures.

**Test resources** (`MachineResourceCoordinator`, behind the internal
`ITestResourceGate`) schedule test projects, not runs. `TestResourceInference`
resolves a project's `TestResourceNeeds` from its direct package references
(`ImpactAnalyzer.GetTestProjectFacts`) and the ordered `testResources` rules:
container packages imply `UsesContainers` and, unless `isolation` is `none`, the
key `project:<repo label>-<sha256(repository)[..12]>/<path relative to the
worktree root>`, which is identical in every worktree of the repository (the
repository is the git common directory, or the worktree root outside git).
`TestResourceKey` normalises `port:`, `container:` and `resource:` keys.
`TestResourceResolver` combines all test projects' needs for a whole-solution
target. `TestRunnerService` acquires a project's resources before its
`TestProcessPool` slot, so a waiting project does not occupy a slot, and releases
them before `OnProjectCompleted`; discovery is not gated.

The coordinator uses `v1/resource-queue/*.ticket`, `v1/resource-leases/*.lease`
and its own `resources.lock`, so admission's scan never sees resource entries.
Entries are named `<seq>_<pid>_<guid>` and held open exclusively like admission
entries. Because held files cannot be read by other processes, each has a
`<name>.json` sidecar (keys, containers, PID, worktree, project, times) written
before the entry is created and deleted after it is closed, both under the lock;
a held entry without a readable sidecar is ignored. On each 100 ms poll it deletes
unheld entries and orphan sidecars, then grants the ticket when no lease shares a
key, no earlier ticket shares a key, and, if it needs containers and
`budget.json` sets `containerSlots`, container leases plus earlier container
tickets are below the limit. Runs take all resources at once and conflicting
tickets are ordered by sequence, so there is no deadlock and no starvation.
Grants create the lease and release the ticket. Blockers (held or queued, with
PID, worktree and project) are reported through `TestRunCallbacks.OnProjectWaiting`
into `PistonState.ProjectResourceWaits` and `ProjectRunStatus.WaitingForResources`.
After the grant, declared `port:` keys are probed by binding a listener; a bound
port is a warning on `TestProjectResources`, not a failure.
`ResourceConflictScanner` matches failed projects' runner errors, failure
messages, output and log against port/container-name conflict signatures and
records `SuspectedResourceConflict` on the `TestRunProjectOutcome`.

Limits: budgets are admission reservations, not OS resource caps. Liveness
means "process alive", so a hung daemon keeps its lease. Orphaned children of a
crashed daemon are not counted. Coordination is per OS user and needs a local
filesystem. Graph loading at startup is not admitted. CI runs the test suite on
Linux and Windows, and the packed-tool samples smoke on macOS as well.

## Coverage and persistence

Coverage is opt-in. The VSTest path requests `XPlat Code Coverage`, so the target
test project needs a compatible collector (such as `coverlet.collector`). The MTP
path passes `--coverage --coverage-output-format cobertura` when the test app
references `Microsoft.Testing.Extensions.CodeCoverage`, running it through
`dotnet test` rather than server mode. Both produce Cobertura XML, which
`CoverageProcessor` parses and merges across reports, dropping files outside the
solution directory. A probe of the current MTP coverage extension stalled in
server mode without producing test updates or a report; see the
[MTP server-mode investigation](docs/mtp-server-mode.md) before changing this
fallback.

When enabled, `SqliteCoverageStore` creates `<solution-directory>/.piston/piston.db`
and uses WAL mode. Its tables are:

| Table | Stored data |
|---|---|
| `coverage_files` | Per-file line/branch totals, module, content and snapshot hashes, stale and partial flags. |
| `coverage_lines` | Per-line hits and branch counts: the merged report. |
| `coverage_sets` | One row per file-level attribution set: run ID and granularity (always `run`). |
| `coverage_set_tests` | The tests credited with a set. |
| `coverage_set_lines` | The lines a set covers, with stale flag. |
| `coverage_summary` | File path, last run ID, last-updated timestamp. |
| `impact_units` | One row per impact unit: test project, granularity, unit key, run ID, dirty flag. |
| `impact_unit_tests` | The tests a unit measured. |
| `impact_unit_files` | Per unit and file: executed member keys, fallback spans and the snapshot they were measured against. |
| `impact_snapshots` | Deduplicated file content by hash, shared by the report and impact map. |

The report tables answer the dashboard, `get_coverage`, changed-line coverage and
exports. A full, unfiltered run replaces them. A selective run merges its hits
into files whose source is unchanged, or remaps earlier hits through unchanged
C# members when source has moved, provided non-member syntax is unchanged. Only
one-to-one line mappings preserve counts; ambiguous split or joined lines remain
stale until remeasured. Hits from edited members are dropped. Unvalidated
current lines are returned as stale ranges; changed-line coverage can still report
lines whose member identity and body are unchanged. Exports continue to omit stale
files.

The set tables hold coarse file-level attribution: every covered line of a run is credited to every test of that run. Tests that share one
line list share one set, so a run stores its covered lines once rather than once
per test. Processing replaces old associations for files present in new coverage
data and prunes sets left without lines. Hit counts are association values, not
independently measured per-test execution counts. Databases from older versions
held a per-test `coverage_map` table; it is dropped on startup and refilled by the
next coverage run. A schema-version change drops these derived coverage caches;
the next coverage run and idle backfill rebuild them. The store serializes all use of its connection, since the
orchestrator and the impact analyzer call it from different threads.

Clearing results deletes every coverage table's rows transactionally using a
separate connection, including when collection is currently disabled. No database
is created just to clear results. The storage boundary is the solution directory:
solutions in the same directory share this database and its reset scope; other
solution directories are unaffected.
Within an engine, coverage processing and its availability-state publication
are serialized with clearing, so a cleared commit cannot later publish a stale
coverage indicator.

The engine keeps test results, timings, and build state in memory. Coverage run
IDs are seeded from the database, but they do not constitute test-run history.

Run history (`RunHistoryStore`) is the durable test-result record. It is a JSON
file outside the checkout, resolved by `HistoryLocation` as
`<base>/<repository>-<hash>/<worktree>-<hash>/<solution>.history.json`. The
repository is the git common directory, so linked worktrees share a folder but not
a file. `<base>` is `PISTON_HISTORY_DIRECTORY` or `Piston/history` under local
application data; a location inside the worktree is refused, because its writes
would change the source fingerprint. The engine opens it after acquiring worktree
ownership and holds an exclusive `.lock`; a second opener is read-only and never
saves. `CompleteRun` records every finished run with passed, failed or skipped
tests, classifies each failure against the earlier entries (`New`,
`PreExisting`, `Flaky` or `Unknown`) and attaches the result to the
`TestRunRecord` as `FailureClassifications`. A test is flaky only when it passed
and failed on the same `SourceAfter` fingerprint, and only `Current` runs record a
fingerprint. Outcomes on that fingerprint recorded by other worktrees of the
repository count too: `SiblingHistories` reads the sibling history files in the
repository folder (read-only, shared, cached by write time) outside the store's
lock. A test that both passed and failed across in-run retries is flaky even
without a fingerprint, since every attempt used the same build. Each flaky
classification carries `FlakinessEvidence` (counts, source, retry outcomes, other
worktrees and the newest outcomes). With `retryFailures` set, the orchestrator
re-runs a run's failed tests (`PistonOrchestrator.Retries.cs`) after test
execution and before coverage processing and the post-run fingerprint, filtered
to those tests and without coverage; the final attempt is the result and the
earlier ones are `TestResult.Attempts`. The store writes one entry per attempt,
and per-test entry limits count attempts. Saves are debounced and atomic (temp file then rename); each test
keeps 20 entries, the file keeps 50 runs with per-test results and clipped errors,
and tests unseen for 30 days are pruned. The file stores each project and test
name once and runs in columns. A history failure is logged and never fails a run.
`clear_results` does not clear history. The dashboard reads the kept runs with
`history/getRuns` and `history/getRun`, and compares two runs in the browser
(`RunComparison`). `history/getTestMarks` gives each test whose latest recorded
result failed its classification (`GetLatestFailures`) plus the flaky tests, for
the Tests tab's badges and filters; `history/getTestHistory` gives one test's
entries for its history strip.

There is no run-metadata table, preferences table, or graph cache. Coverage persistence alone is not full
controller-state recovery. `.piston.json` is the actual optional configuration
file; `.piston/config.json` and a user-global configuration hierarchy are not
implemented. The engine also writes a diagnostic log under `.piston`.

CLI solution resolution uses an explicit argument first, then `solution` from
the current directory's `.piston.json` (relative to that config), then unique
solution discovery in the current directory. The selecting config is retained
when it points to a solution elsewhere; web auto-start forwards that config
directory to its daemon through an internal CLI option. With an explicit argument, config is
loaded from the explicit solution's directory instead. Invalid config or a
missing/invalid configured solution is reported rather than silently falling
back. Config is read as a JSON stream so hidden `.piston.json` files are not
excluded by a physical file provider.

## MCP behavior and limitations

The same tools are served over stdio (`piston mcp`) and the daemon's HTTP endpoint.
Test tools (`TestTools`): `verify`, `run_tests`, `get_test_run`, `get_test_results`,
`get_test_history`, `set_test_filter` and `clear_results`. Coverage and selection
tools (`CoverageTools`): `get_coverage`, `get_changed_line_coverage`,
`export_coverage`, `plan_affected_tests` and `run_affected_tests`. They use the same
engine state and pipeline as other clients; MCP does not bypass builds.
`get_changed_line_coverage` (`ChangedLineVerdicts`) classifies each changed line
from the coverage report, the impact map and the latest test results. Stale lines
and outdated impact evidence never count as covered; in a changed C# file, only
lines outside the store's stale ranges (unchanged members, remapped) get verdicts.
Because the impact map records executed methods, a line is covered by passing tests
only when every current test credited with it passed. A credited test's result is
looked up in its own project's suite by solution-relative project ID. A run stores its coverage before it replaces
`TestSuites`, and marks that window with `PistonState.BeginResultsUpdate`/`EndResultsUpdate`.
The classifier rereads when `ResultsVersion` changes during a read, or when the changed
lines or the changed files' content differ after it (line numbers must describe the content
coverage was judged against). Coverage is read off-thread with a bounded, cancellable wait,
since the store's lock is held while a run stores coverage. If the state does not settle,
every changed line is `unknown` (`resultsUpdating`, or `stale` while files keep changing)
rather than combine two runs or two versions of a file. Test
processes are fresh unless `warmTestHosts` is enabled (see
[Builds and execution](#builds-and-execution)).

`run_tests` requests a full run, awaits that specific engine request with
timeout/cancellation, and returns a textual summary and its run ID. Completion
does not depend on polling the aggregate phase or prior results. `get_test_results` reads
current accumulated state without initiating a run.

Run provenance: `PistonOrchestrator` creates a `TestRunRecord` for every
run request and publishes it as `PistonState.CurrentRun`. When the run finishes, the
record becomes `LastRun` and is kept in a bounded in-memory `RecentRuns` list. Each record holds:

- the run context;
- `SourceFingerprinter` fingerprints from before and after the run;
- watcher-observed changes;
- its own build result (copied errors/warnings, status and duration), never borrowed from `LastBuild`;
- per-project outcomes;
- its own suites. Aggregate suites carry their originating `RunId`.

`run_tests` returns the new run ID. `get_test_run` serializes a record as
`piston.test-run/v1`. The protocol `StateSnapshotNotification` carries optional
`LastRun`/`ActiveRun` (`TestRunDto`, test counts plus optional `Admission` and run-local `Build`), and
`TestSuiteDto` carries an optional `RunId`; older clients can ignore all of them.
MCP adds `build` (null before a build result is captured) to the existing v1
document. Raw diagnostic lines retain MSBuild's ID and file/line/column location.
A build failure uses the same source-freshness computation as test completion;
`Current` build diagnostics do not make `isCurrentFullResult` true.

Every test suite also carries its short display `Name` and a `ProjectId`: the
project path relative to the solution directory, with `/` separators and the
project-file extension (for example, `tests/One/Tests.csproj`). The ID is the
key for `engine/runTests`, `tests/getRunLog`, history, and dashboard selection;
short names are not accepted for targeted requests. The dashboard displays the
short name unless another project shares it, in which case it adds the relative
path; labels normalize daemon path separators regardless of the browser's platform.
Duplicate test IDs are detected within each project, not across projects. When
the runner falls back to a solution target without project IDs, suite names remain
the fallback identities for logs and flattened test results.
Run history format v2 stores project IDs, and retained history remains queryable
by ID after a project is renamed or removed. On loading v1 history, a short
name is migrated only when it identifies exactly one current test project,
after a successful build and post-restore graph refresh. While project discovery
is incomplete, legacy data is left unchanged and new runs are not recorded in
history, with a diagnostic explaining why. Ambiguous or removed projects are dropped only once
the complete project list is available.

`set_test_filter` changes the active filter but does not itself run tests.
`clear_results` clears in-memory suites, the last-run timestamp, coverage state,
and persisted coverage in the current solution directory. It does not cancel an
active pipeline; a subsequent completion can publish fresh results. It does not
clear [run history](#coverage-and-persistence), which `get_test_history` and
`verify`'s failure classification read.

## Build processes and engine test setup

Engine-test setup process helpers drain both stdout and stderr and enforce a
90-second deadline. A small test-only process host establishes a Unix session or
Windows kill-on-close job before starting the command. Timeout cleanup terminates
the session/job even if the command has exited but a descendant still owns an
output pipe. A bounded cleanup wait reports command, host PID, working directory,
and the latest 50 lines from each stream (not a globally ordered transcript).
The helpers also reject unsuccessful setup commands instead of
discarding their exit codes (the optional MTP availability probe retains its
nonzero-exit handling, but no longer swallows timeouts).

A concurrent engine-suite reproduction found setup restores and an engine build
waiting in `Process.WaitForExitAsync`'s output-EOF drain after the child had exited,
not in MSBuild solution evaluation. Both setup commands and engine builds set
`MSBUILDDISABLENODEREUSE=1` in the child environment to prevent persistent MSBuild
nodes retaining those redirected pipes. This trades cross-build MSBuild node
reuse for reliable process completion; it does not shut down shared build servers
or disable concurrency within a build. The timeout remains a guard for other
setup stalls, including restore/network or filesystem locking.

## Non-goals and direction

Piston is a test runner that agents combine with independent code-intelligence
tools; it is not a replacement IDE or a Roslyn service. Each worktree has one
owning process (a `piston mcp` session or a daemon); there is no shared per-user
hub. Machine-wide coordination is limited to the file-lock ownership and
admission budget described above.

Aspire is one possible integration-test example for the generic runner. There
is no Aspire-specific lifecycle, service orchestration, or dedicated
integration contract. Planned work, including a native Copilot canvas for the
dashboard, is in [docs/ROADMAP.md](docs/ROADMAP.md).
