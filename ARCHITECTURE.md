# Piston Architecture

## Scope and status

Piston is an agent-facing continuous test backend for .NET, with a CLI daemon,
HTTP MCP test tools, and a web dashboard. It builds and tests in the watched
checkout using standard `dotnet` commands, not shadow workspaces or IL rewriting.

This document separates implemented behavior from proposed work. The Phase 0
build-correctness and [surface reduction/tool packaging (#4)](https://github.com/damianh/piston/pull/4)
changes described below are **pending companion changes**, not features already
shipped by this documentation change.
Until those changes land, the checkout still includes the Roslyn worker/tools,
diagnostic UI, VSCode extension, and desktop installer release workflow.

## Components

| Project | Responsibility |
|---|---|
| `Piston` | CLI: launch/attach to daemon, open browser, foreground daemon, stop/status. |
| `Piston.Hosting` | Daemon composition, protocol router, web hosting, configuration, MCP call tracking. |
| `Piston.Engine` | Watcher, impact analysis, builds, test execution, current state, coverage processing. |
| `Piston.Protocol` | JSON-RPC contracts, DTOs, framing, named-pipe and stdio transports. |
| `Piston.Mcp` | HTTP MCP server and test tools backed by the engine. |
| `Piston.Web` | Blazor WebAssembly dashboard connected over WebSocket. |
| `Piston.Desktop` | Existing Photino host; source is to be frozen in Phase 0, not the primary delivery surface. |

The current checkout additionally has `Piston.Roslyn`, `Piston.Roslyn.Worker`,
Roslyn MCP tools, and `extensions/vscode`. Phase 0 removes these and diagnostic
polling/UI/contracts rather than maintaining a second code-intelligence backend.
The retained MCP surface is test-oriented; clients can use their own code
intelligence alongside Piston.

The retained dependency shape after the companion reduction is:

```text
Piston (CLI) ------> Piston.Hosting ------> Piston.Engine ------> Piston.Protocol
                           |
                           +------------> Piston.Mcp ----------> Piston.Engine
Piston.Web -----------------------------------------------> Piston.Protocol
Piston.Desktop ---> Piston.Hosting  (frozen source)
```

The CLI runs the daemon in a separate process. The existing desktop host embeds
`DaemonHost` in-process. Neither arrangement implements a per-user hub with
isolated worktree workers.

## Transports and clients

The daemon routes JSON-RPC requests and notifications over named pipes, stdio,
and WebSocket. The browser uses `/ws`. HTTP MCP is a separate, optional agent
endpoint enabled with `--mcp-port` or `mcpPort` in `.piston.json`.
For port 5200, its URL is `http://localhost:5200`, not `/mcp`.

Implemented protocol operations include stop, force run, set filter, clear
results, state retrieval, and MCP call-log retrieval. `engine/start` is rejected
by the headless dispatcher because the solution is configured at launch.
The file-coverage request currently returns an empty line list; the presence of
coverage contracts does not establish a working line-coverage client API.
Clients receive state, test progress, build errors, and activity notifications.
The dashboard presents current test/build state, activity, and MCP call history;
the diagnostic view is removed by the Phase 0 companion change.

See [README.md](README.md) for executable commands and the actual configuration
shape. The protocol's stdio transport is not an MCP stdio server.

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

The watcher batches changes to `.cs`, `.csproj`, `.props`, and `.targets`, excluding
build outputs and other directories such as `.git` and `node_modules`. Although
the analyzer recognizes solution-file changes, the watcher's filters do not
currently include `.sln`, `.slnx`, or `.slnf`.

A new trigger cancels the preceding pipeline, and a semaphore serializes runs.
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

### Coverage-assisted filtering (implemented, coarse)

The existing Tier 3 path attaches test FQNs when all changed C# files have
non-stale coverage associations. It queries **whole files**, not changed line
ranges. The store has a line-range query API, but the analyzer does not use it
and does not calculate changed lines with a git diff.

The important limitation is attribution: the orchestrator passes all reports
and all returned test FQNs from a run to `CoverageProcessor`. The processor unions
the covered lines across those reports and associates **every covered line with
every supplied test FQN**. Associations can therefore span multiple suites in a
run. Stored FQNs do not establish that a particular test executed a particular
line, even though the data is represented as file/line/test rows.

This is coarse coverage-assisted selection, not verified fine-grained per-test
impact detection. If a changed file lacks usable coverage associations, selection
falls back to project-level tests. The analyzer marks file associations stale
after reading them; coverage processing refreshes files represented in new data.
This is not a complete freshness or correctness guarantee across arbitrary edits
or restarts.

Fine-grained coverage is a future spike: establish trustworthy attribution,
evaluate changed-line mapping and invalidation, and measure selection correctness
before claiming per-test precision. This limitation concerns Piston's current
processing path, not every capability offered by test frameworks or collectors.

## Builds and execution

Builds use standard `dotnet build`. Full runs target the original solution.
Project-targeted builds are sequential at Piston's orchestration level;
MSBuild's internal scheduling is distinct from Piston launching independent
builds concurrently. No parallel-build latency claim is established here.

**Pending Phase 0 correction:** selective runs must rebuild the affected test
projects and their dependencies before running tests with `--no-build`. Building
only changed production projects can leave stale test outputs. The concrete
target-selection and restore behavior will be documented from the companion
implementation rather than inferred from the impact-analysis output.

Both execution backends launch new `dotnet test` processes:

| Backend | Current mechanism |
|---|---|
| VSTest | Project/solution target, `--no-build`, normal verbosity, TRX results; stdout progress parsing. |
| Microsoft.Testing.Platform (MTP) | `--project`, `--no-build`, detailed output; results parsed from stdout. Requires the appropriate SDK runner configuration in the solution's `global.json`. |

When the graph identifies an MTP output path, the composite strategy selects
MTP; otherwise it selects VSTest. `Auto` and the currently named `Process` mode
both use this routing. `InProcess` is accepted as a configuration enum value but
throws `NotSupportedException` during engine construction. There is no implemented
in-process MTP execution mode.

`TestProcessPool` is a semaphore-based concurrency limiter, not a pool of
prewarmed, reusable processes. Automatic concurrency is
`Math.Max(1, Environment.ProcessorCount / 2)`. `processRecycleAfter` logs a debug
warning after a configured number of runs; it does not recycle persistent hosts.
There is no per-test or per-class process-isolation option. Test-framework
parallelism inside each process is separate from Piston's project concurrency.

## Coverage and persistence

Coverage is opt-in. The VSTest path requests `XPlat Code Coverage`, so the target
test project needs a compatible collector (such as `coverlet.collector`). It
parses Cobertura XML. The current MTP path does not pass coverage arguments or
return coverage reports, so enabling `--coverage` does not make that backend
produce coverage data.

When enabled, `SqliteCoverageStore` creates `<solution-directory>/.piston/piston.db`
and uses WAL mode. Its implemented tables are:

| Table | Stored data |
|---|---|
| `coverage_map` | Run ID, test FQN, file path, line number, hit count, stale flag. |
| `coverage_summary` | File path, last run ID, last-updated timestamp. |

Processing replaces old associations for files present in new coverage data.
Rows reflect the coarse attribution described above; hit counts are association
values, not independently measured per-test execution counts.

The engine keeps test results, timings, and build state in memory. Coverage run
IDs are seeded from the database, but they do not constitute test-run history.
There is no implemented durable test-result/failure history, run-metadata table,
preferences table, or graph cache. Coverage persistence alone is not full
controller-state recovery. `.piston.json` is the actual optional configuration
file; `.piston/config.json` and a user-global configuration hierarchy are not
implemented. The engine also writes a diagnostic log under `.piston`.

## MCP behavior and limitations

The retained MCP wire names are `run_tests`, `get_test_results`,
`set_test_filter`, and `clear_results` (implemented by the corresponding
PascalCase methods in `TestTools`). They use the same engine state and pipeline
as other clients; MCP does not bypass builds or provide prewarmed test processes.

`run_tests` requests a full run and returns a textual summary. The pending
packaging companion awaits the engine run with timeout/cancellation and removes
redundant phase polling, so normal completion in `Watching` can return. This
documentation change does not implement that fix. `get_test_results` reads
current accumulated state without initiating a run.

`set_test_filter` changes the active filter but does not itself run tests.
`clear_results` currently clears in-memory suites and the last-run timestamp,
not the persisted coverage database, despite its tool description. Structured
agent contracts, freshness semantics, and reliable run completion are future
backend work, not guarantees of the existing text-based tools.

## Proposed direction (not implemented)

The focus is a reliable test backend that agents can combine with independent
code-intelligence tools, not a replacement IDE or a Roslyn service.

Future work includes a measurement harness with an approved methodology,
fine-grained coverage experiments, explicit run/freshness contracts, and
Copilot canvas/harness integrations. None is an existing Piston feature, and
performance budgets are hypotheses until measured against representative
solutions and correctness checks.

A per-user hub with an isolated worker per worktree is also proposed. Current
pipe names are derived from solution paths and each daemon builds in its watched
checkout; this is not worktree isolation or a shared hub.

Aspire is one possible integration-test example for the generic backend. There
is no implemented Aspire-specific lifecycle, service orchestration, or dedicated
integration contract. Implementation priorities and approval gates belong in the
separately maintained roadmap.
