# Piston

An agent-facing continuous test backend for .NET, with a headless daemon, HTTP MCP test tools, and a web dashboard.

Piston watches a .NET solution, selects affected test projects, builds, runs test projects with bounded concurrency, and publishes results to connected clients. It uses standard `dotnet build` and `dotnet test` in the watched checkout.

**Current structure:** the `Piston` .NET tool owns CLI commands, daemon hosting,
and HTTP MCP tools. `Piston.Engine` is the separate MSBuild-loading and test-execution
boundary; `Piston.Protocol` provides shared wire contracts and transports, and
`Piston.Web` is the existing dashboard. Desktop source and its project have been
deleted. The hub/worker split and later UI/canvas decisions remain deferred.

## Requirements

- .NET 10 SDK
- A `.sln`, `.slnx`, or `.slnf` solution with compatible test projects (for example xUnit, NUnit, or MSTest)
- The SDK and test adapters/extensions required by the target solution; MTP projects need the appropriate `global.json` test-runner configuration

## Delivery

The primary surface is the CLI daemon and its test APIs, distributed as a
framework-dependent .NET tool. Desktop trial installers and source are no longer
maintained or shipped.

Build and install the local .NET tool:

```sh
dotnet pack src/Piston/Piston.csproj -c Release -o packages
dotnet tool install Piston --version 0.1.0 --tool-path ./tools --source ./packages --no-cache
./tools/piston --help
./tools/piston daemon /path/to/solution.slnx --mcp-port 5200 --web-port 5199
```

Use an actual solution path in the last command. On Windows, the executable is
`.\tools\piston.exe`. Package ID is `Piston`, command name is `piston`, and `0.1.0`
is the local development package version; use the built/released version when
it differs. The package is framework-dependent and requires the .NET 10 SDK
and ASP.NET Core 10 runtime, plus the target solution's own SDK requirements.
It bundles the web dashboard; it is not an MCP-specific package or a `dnx`
entry point.

The release workflow attaches a `.nupkg` to GitHub Releases rather
than desktop installers. NuGet publication is a separate, opt-in workflow path
requiring package ownership and credentials; these local commands do not imply
that a package is already published on NuGet.org.

## Usage

Examples below assume `piston` is on `PATH`; with the isolated install above,
substitute `./tools/piston` (or `.\tools\piston.exe` on Windows).

```sh
# Start (or attach to) the daemon and open the web dashboard
piston [<solution>] [--pipe-name <name>] [--web-port <port>]

# Run the daemon in the foreground
piston daemon [<solution>] [options]

# Manage a running daemon
piston stop [<solution>]
piston status [<solution>]
```

`piston status` prints the daemon's PID, solution, phase, web and MCP URLs, and its latest and active run. `piston stop` asks the daemon to shut down, then waits for its process to exit. It exits 0 only once the process has exited, so the ports are free; with no daemon running it prints `No daemon running` and also exits 0. Startup failures exit with code 1 and an `error:` message: a worktree that already has a daemon, or an occupied `--mcp-port`/`--web-port`. Endpoints bind after worktree ownership is taken and before the startup run, so a port conflict starts no build or test run.

`<solution>` is a path to a `.sln`, `.slnx`, or `.slnf` file. An explicit argument wins; otherwise Piston uses the `solution` field in the current directory's `.piston.json`, resolved relative to that file. Without a configured path, exactly one solution must exist in the current directory; multiple candidates require an explicit or configured path. A missing or invalid configured solution is an error, not a discovery fallback.

**`piston daemon` options:**

| Option | Description |
|---|---|
| `--debounce <ms>` | File-change debounce interval in milliseconds. |
| `--filter <pattern>` | Test filter passed to the selected test runner on startup; syntax depends on the runner. |
| `--coverage` | Request coverage collection on the VSTest path; requires a compatible collector in the test project. |
| `--parallelism <n>` | Max concurrent test processes. `0` = auto. |
| `--stdio` | Use stdin/stdout for engine JSON-RPC transport, not MCP. |
| `--no-stdio` | Force named-pipe transport, overriding `.piston.json`'s `stdio` setting. |
| `--pipe-name <name>` | Override the named pipe name (default: derived from solution path). |
| `--mcp-port <port>` | Enable the MCP server on the specified port. |
| `--web-port <port>` | Port for the web UI and WebSocket server (default: 5199). |

**Examples:**

```sh
# Auto-discover solution, start daemon, open dashboard in browser
piston

# Explicit solution path
piston ./src/MyApp.slnx

# Foreground daemon with coverage and MCP enabled
piston daemon --coverage --mcp-port 5200
```

## Web dashboard

The daemon serves a Blazor WebAssembly dashboard (default `http://localhost:5199`) connected over WebSocket. It shows test results, build errors, engine activity, and MCP tool-call history. The Roslyn diagnostic view has been removed.

## MCP server (AI agents)

With `--mcp-port` (or `mcpPort` in `.piston.json`), the daemon exposes an HTTP MCP endpoint backed by the running engine. For `--mcp-port 5200`, connect at `http://localhost:5200` (the root endpoint, not `/mcp`). The daemon retains current state, but runs still launch `dotnet build` and fresh `dotnet test` processes; there is no prewarmed test-process pool.

To set up a coding agent, follow [docs/agent-usage.md](docs/agent-usage.md). It covers setup, the verification contract and a custom-instructions snippet.

| Tool | Description |
|---|---|
| `verify` | Get current full verification. Reuses the latest run if it is a current, full-scope, unfiltered result for the present source fingerprint; otherwise awaits an active full run or forces one, retrying at most twice when the source changes mid-run, then reporting `Stale`. Returns a compact verdict (`Passed`, `TestsFailed`, `BuildFailed`, `Incomplete`, `Stale`, `Cancelled`, `Faulted`, `TimedOut`, `NotFullScope`) with run ID, fingerprint, counts, capped failing tests / build errors and queue info. Only `Passed` is proof. |
| `run_tests` | Request a full build + test run; returns a textual summary plus a `Run:` line with the run ID, status, scope, freshness and source fingerprint. |
| `get_test_run` | Read one run's provenance, build diagnostics and per-test results as JSON (`piston.test-run/v1`): `runId` = an ID, `latest` (default) or `active`. |
| `get_test_results` | Read current accumulated test results (may combine several runs; each suite shows its run ID). |
| `set_test_filter` | Apply a test name filter. |
| `clear_results` | Clear current test suites, last-run timestamp, coverage state, and persisted coverage in the current solution directory. A running test run may publish new results afterwards. |

These are the retained test tools. `LoadWorkspace`, `GetDiagnostics`,
`SemanticSearch`, `GetAst`, `Rename`, and `NotifyFileChanged` have been removed;
Piston does not own Roslyn workspace synchronization.

`run_tests` awaits the specific engine request with timeout/cancellation and
returns that run's record, rather than polling an aggregate phase or reading
results from an earlier run. `get_test_results` reads
accumulated state without starting a run; preserved results from unaffected
suites are not proof that every test ran after the latest edit.

### Run provenance

Every run, including startup and file-change runs, gets a record in the
`piston.test-run/v1` JSON format that `get_test_run` returns. The record holds:

- a unique `runId` and a sequence number;
- the trigger (`Startup`, `Forced` or `FileChange`);
- the status: `Completed`, `Incomplete`, `BuildFailed`, `Cancelled` or `Faulted` (`Queued` or `Running` while active);
- for runs that went through machine-wide admission, an `admission` object (see below);
- the scope (`Full` or `Selective`), `isFiltered` and the effective filter;
- the run context: solution, test projects, build targets, configuration, SDK, runtime and OS;
- run-local `build` output (status, raw MSBuild errors/warnings including diagnostic IDs and locations, duration), or null if the build did not return;
- per-project outcomes, duplicate test IDs, and every test's fully qualified name and status.

The source fingerprint (`piston-source-v2`) is a SHA-256 over every file under the solution directory except `bin`, `obj`, `TestResults`, `.git`, `.vs`, `.idea`, `.piston` and `node_modules`. It also covers the nearest ancestor `global.json`, `Directory.Build.*`, `Directory.Packages.props` and `NuGet.config`. Paths and contents are length-prefixed (unsigned 64-bit big-endian byte lengths), so embedded NUL bytes cannot collide with file boundaries. Capture is attempted before and after every started run, including cancellation and faults; unavailable fingerprints are reported as `Unknown`. Queued requests cancelled before starting have no fingerprints. A run is `Stale` if the fingerprint changed or the file watcher saw a change during the run; a watcher change also cancels the run.

`isCurrentFullResult` is true only for a `Completed`, unfiltered, `Full`-scope, `Current` run with no duplicate IDs or unfinished (`NotRun`/`Running`) tests. Selective or filtered runs never qualify, and a filter can turn even a forced run into a filtered one.

A `BuildFailed` run can have `Current` build diagnostics but is never a current full
**test** result. Its `build` is captured from that run, not the aggregate `LastBuild`,
and is also available as `TestRunDto.Build` in protocol snapshots. Consumers must
check the requested run ID, solution path, before/after source fingerprints and
freshness before treating a diagnostic as evidence for their source generation.

Limits:

- Only the 16 most recent runs are kept, and only in memory.
- The fingerprint does not cover sources outside the solution directory, installed SDKs, the NuGet cache, or merged user/machine NuGet configuration.
- `verify` reevaluates project inputs and returns `NotFullScope` for user projects, imports or declared files outside the fingerprint (including excluded directories). SDK/workload files, packages and generated restore imports remain toolchain/cache exclusions. Evaluation failure is `Faulted`; neither verdict is a pass or a hidden fallback.
- Full runs refresh the solution graph before selecting test projects. `verify` also rejects an otherwise current run if its selected projects omit a test project from the refreshed graph.
- The SDK version is recorded from `dotnet --version` run in the daemon's working directory.
- `context.workingDirectory` is the build/SDK-probe directory. `context.testWorkingDirectories` records each test target's actual directory: the solution directory for MTP, the daemon directory for VSTest.

### Worktree ownership and machine execution budget

Daemons for the same OS user coordinate through a per-user directory: `$PISTON_EXECUTION_DIRECTORY` if set (must be absolute), otherwise `Piston/execution` under the local application-data folder (`~/.local/share/Piston/execution` on Linux). It must be on a local filesystem. At startup the daemon creates or tightens its Unix coordination directories to mode 0700 and checks that file locks are enforced there. If they are not (for example `DOTNET_SYSTEM_IO_DISABLEFILELOCKING` is set), the daemon refuses to start.

- **One owner per worktree.** A daemon takes an exclusive lock for its solution's worktree: the nearest ancestor that contains `.git`, after resolving symlinks, or the solution directory if there is none. A second daemon for any solution in the same worktree exits with an error that names the owner's PID, solution and start time. The OS releases the lock when the owner exits or crashes, so a stale owner never blocks a restart.
- **Machine-wide budget.** Before building, each run (startup, forced or file-change) asks for admission. It reserves the daemon's test-process parallelism as CPU slots, plus `execution.memoryMb` if configured. Runs are admitted in strict FIFO order, only while the sum of admitted reservations fits the budget. The budget is read from `budget.json` in that directory on every admission check:

  ```json
  { "cpuSlots": 8, "memoryMb": 16384 }
  ```

  Without the file, the budget is the logical processor count with no memory limit. When the file exists, `cpuSlots` is required and `memoryMb` is optional. An unreadable file is an error, not a default budget. Unknown properties, non-positive values, or a reservation that could never fit fail explicitly: at daemon startup, or as a `Faulted` run if the file changes while a run waits. If the budget limits memory, every daemon must set `execution.memoryMb`. MCP text also reports configured memory reservations when memory is unbudgeted, explicitly marked `unbudgeted`.

  Update `budget.json` by writing the complete JSON to a temporary file in the same directory, then atomically renaming it over `budget.json` (without deleting the old file first). This is recommended over editing in place. Empty/partial JSON and I/O failures are retried up to five times at 20 ms intervals (100 ms total delay); persistent failures still report the existing invalid-budget error. Schema errors and access denial fail immediately.
- **Visible queueing.** A waiting run is published as the active run with status `Queued` and an `admission` object: `state` (`Queued`/`Admitted`), `queuedAhead`, `queueLength`, `otherAdmittedRuns`, `cpuSlotsInUse`, `memoryMbInUse`, the reservation, the budget and its source, `queuedAt` and `admittedAt`. It appears in `get_test_run` (`active` or by ID), in the `Active Run:` line of `get_test_results`, and in `ActiveRun`/`LastRun` of the protocol state snapshot. A run superseded while queued ends `Cancelled` without starting, and its replacement keeps the queue position. Leases and tickets are released when a run ends, is cancelled or faults, or when the daemon exits or crashes.

Graceful engine disposal waits for cancelled pipelines to finish and release their admission leases before releasing worktree ownership. Build/test cancellation waits for the killed runner to exit.

Limits: this is admission control by reservation. Piston does not cap the actual CPU or memory of builds or test processes (no cgroups or job objects). A hung but live daemon keeps its lease. Child processes orphaned by a crashed daemon are no longer counted. Daemons of different OS users do not share a budget. Solution loading at startup is not admitted. Owner `.lock` files are left in place on purpose. Local validation covers Linux; CI is configured for Linux and Windows. macOS execution has not been validated.

## Coverage and impact selection

Project ownership and transitive dependencies determine affected test projects. The optional Tier 3 path adds a file-based test-FQN filter when usable coverage exists. It is **not verified per-test/changed-line selection**: `CoverageProcessor` unions covered lines across reports and attributes them to every test returned in that run. The analyzer queries whole files, not changed line ranges.

Coverage associations persist in `.piston/piston.db`; test-result history and the project graph do not. The VSTest runner requests `XPlat Code Coverage` (for example via `coverlet.collector`). The current MTP runner does not request or return coverage reports. Fine-grained attribution and selection correctness are future experiments.

### Selective builds

Piston builds the union of affected production and
test projects with one temporary solution-filter build, allowing restore and
MSBuild dependency scheduling before no-build test execution. It preserves
`.sln`/`.slnx` solution context and `.slnf` membership. Unsupported/unreadable
solution paths or non-member targets fall back to sequential per-project builds
with `--no-restore`; a selective run with no affected tests builds only. Full
runs still build the original solution.

## Configuration file

With an explicit solution argument, Piston reads the optional `.piston.json` in
that solution's directory. Without an argument, it reads `.piston.json` in the
current directory before resolving the solution and retains that config even
when `solution` points into another directory, including when the web command
auto-starts a daemon. Invalid config files are reported
as errors rather than silently ignored.
Supplied CLI values override corresponding config values, with two caveats:
`--coverage` enables coverage but cannot disable a config-enabled value, and
non-positive debounce/parallelism values fall back to config/defaults. There is
no implemented `.piston/config.json` or user-global config hierarchy.

```json
{
  "debounceMs": 500,
  "testFilter": "MyNamespace",
  "coverageEnabled": true,
  "parallelism": 4,
  "mcpPort": 5200
}
```

| Field | Type | Description |
|---|---|---|
| `solution` | `string` | Default solution path, relative to the config file (or absolute). An explicit CLI solution argument overrides it. |
| `debounceMs` | `int` | File-change debounce interval in milliseconds. |
| `testFilter` | `string` | Default filter passed to the selected test runner. |
| `coverageEnabled` | `bool` | Request VSTest coverage collection. |
| `parallelism` | `int` | Max concurrent test processes. `0` = auto: half the logical processor count, at least one. |
| `processRecycleAfter` | `int` | Runs after which a pool slot logs a recycling warning (default 50). |
| `pipeName` | `string` | Override the named pipe name. |
| `stdio` | `bool` | Use stdin/stdout JSON-RPC transport. |
| `mcpPort` | `int` | Enable the MCP server on this port. |
| `testExecutionMode` | `string` | `"Auto"` and `"Process"` currently both route between MTP and VSTest. `"InProcess"` is not implemented and throws at startup. |
| `execution.memoryMb` | `int` | Memory in MiB each run reserves from the machine execution budget (see [Worktree ownership and machine execution budget](#worktree-ownership-and-machine-execution-budget)). Must be positive; required when the budget sets `memoryMb`. A reservation, not a cap. |

## Building from source

Production code lives in four projects under `src/`: `Piston`, `Piston.Engine`,
`Piston.Protocol`, and `Piston.Web`. Hosting and MCP are folders within `Piston`,
not separate assemblies. The engine emits typed activity records; the tool maps
them to protocol DTOs without making the engine depend on `Piston.Protocol`.
`Piston.Tests` covers the tool, host, protocol, and MCP; `Piston.Engine.Tests`
covers execution and retains the two process-isolation helper executables.

```sh
git clone https://github.com/damianh/piston.git
cd piston
dotnet build Piston.slnx

# CLI
dotnet run --project src/Piston -- [args]
```

Run tests:

```sh
dotnet test Piston.slnx
```

Engine-test setup `dotnet restore`/`build` helpers have a 90-second timeout,
drain both output streams, and fail with command, PID, directory, and recent
output diagnostics. On timeout they terminate the
command's whole Unix session or Windows job, including descendants that
outlive their parent. Setup
commands and engine builds disable MSBuild node reuse in their child environment
to prevent persistent nodes retaining redirected output pipes after `dotnet` exits.

### AI agent tooling (roslynk)

The repo pins [roslynk](https://www.nuget.org/packages/roslynk) as a local .NET tool and registers it in `.mcp.json`, so Copilot CLI / agent sessions get semantic C# intelligence (diagnostics, find references, rename, code fixes) over `Piston.slnx`. Run `dotnet tool restore` once after cloning.

## Proposed direction

Fine-grained coverage, Copilot canvas/harness integrations, and a per-user hub
with isolated worktree workers remain deferred. The separate `measurements/`
harness does not establish a measured Piston improvement; a matched comparison
campaign remains future work. Aspire is one possible generic integration-test example, not a
specialized backend. See [ARCHITECTURE.md](ARCHITECTURE.md) for implementation
details and limitations.
