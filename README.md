# Piston

An MCP-based continuous test runner for .NET. Coding agents use it through MCP: a per-session stdio server (`piston mcp`) or the daemon's HTTP endpoint. People use the same engine through a web dashboard.

Piston watches a .NET solution and selects affected tests by project and, with coverage, by changed lines. It builds them, runs the test projects with bounded concurrency and keeps per-test history outside the checkout. Every result is tied to the source it ran against, so an agent can tell a current, full-scope pass from a stale or partial one (`verify`). It uses standard `dotnet build` and `dotnet test` in the watched checkout.

**Structure:** the `Piston` .NET tool owns CLI commands, daemon hosting and the MCP
tools. `Piston.Engine` is the MSBuild-loading and test-execution boundary,
`Piston.Protocol` provides shared wire contracts and transports, and `Piston.Web`
is the dashboard. See [ARCHITECTURE.md](ARCHITECTURE.md) for details and
[docs/ROADMAP.md](docs/ROADMAP.md) for planned work.

## Requirements

- .NET 10 SDK
- A `.sln`, `.slnx`, or `.slnf` solution with compatible test projects (for example xUnit v2/v3, NUnit, MSTest or TUnit; [`samples/`](samples/README.md) has one of each on Microsoft.Testing.Platform)
- The SDK and test adapters/extensions required by the target solution; MTP projects need the appropriate `global.json` test-runner configuration

## Delivery

Piston ships as a framework-dependent .NET tool that provides the stdio MCP server, the daemon (HTTP MCP and the
dashboard) and the CLI.

Build and install the local .NET tool:

```sh
dotnet pack src/Piston/Piston.csproj -c Release -o packages
dotnet tool install Piston --version 0.1.0 --tool-path ./tools --source ./packages --no-cache
./tools/piston --help
./tools/piston daemon /path/to/solution.slnx
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

# Serve MCP over stdio for one agent session (cwd = the session's worktree)
piston mcp [<solution>] [--debounce <ms>] [--filter <pattern>] [--coverage] [--parallelism <n>]

# Manage a running daemon / inspect worktree owners
piston stop [<solution>] [--force]
piston status [<solution>] [--all]

# Export the running daemon's coverage report (stdout unless -o is given)
piston coverage-export [<solution>] [--format lcov|cobertura] [-o <file>]
```

`piston status --all` lists every live worktree owner (PID, `mcp` or `daemon`, solution, worktree) and stale ownership records. If a worktree's owner cannot be reached over the daemon pipe (for example a `piston mcp` process, or a daemon whose worktree was deleted), `piston stop` prints the owner's PID, solution and worktree and exits 1; `piston stop --force` sends SIGTERM and kills the process if it has not exited.

`piston status` prints the daemon's PID, solution, phase, web and MCP URLs, and its latest and active run. `piston stop` asks the daemon to shut down, then waits for its process to exit. It exits 0 only once the process has exited, so the ports are free; with no daemon running it prints `No daemon running` and also exits 0. Startup failures exit with code 1 and an `error:` message: a worktree that already has a daemon, or an occupied `--mcp-port`/`--web-port`. Endpoints bind after worktree ownership is taken and before the startup run, so a port conflict starts no build or test run.

`piston coverage-export` asks the running daemon for its coverage report and writes it as LCOV (default) or Cobertura XML, for editors, CI or ReportGenerator. Files changed since they were measured are left out. Large exports are transferred in bounded chunks from one snapshot, so they can exceed the pipe's per-message size limit without mixing measurements from different runs. It exits 1 when no daemon is running or coverage is disabled.

`<solution>` is a path to a `.sln`, `.slnx`, or `.slnf` file. An explicit argument wins; otherwise Piston uses the `solution` field in the current directory's `.piston.json`, resolved relative to that file. Without a configured path, exactly one solution must exist in the current directory; multiple candidates require an explicit or configured path. A missing or invalid configured solution is an error, not a discovery fallback.

**`piston daemon` options:**

| Option | Description |
|---|---|
| `--debounce <ms>` | File-change debounce interval in milliseconds. |
| `--filter <pattern>` | Test filter passed to the selected test runner on startup; syntax depends on the runner. |
| `--coverage` | Request coverage collection. VSTest projects need a collector such as `coverlet.collector`; MTP projects need `Microsoft.Testing.Extensions.CodeCoverage`. |
| `--parallelism <n>` | Max concurrent test processes. `0` = auto. |
| `--stdio` | Use stdin/stdout for engine JSON-RPC transport, not MCP. |
| `--no-stdio` | Force named-pipe transport, overriding `.piston.json`'s `stdio` setting. |
| `--pipe-name <name>` | Override the named pipe name (default: derived from solution path). |
| `--mcp-port <port>` | Port for the always-on HTTP MCP server (default: 5200). |
| `--web-port <port>` | Port for the web UI and WebSocket server (default: 5199). |

**Examples:**

```sh
# Auto-discover solution, start daemon, open dashboard in browser
piston

# Explicit solution path
piston ./src/MyApp.slnx

# Foreground daemon with coverage, MCP on a non-default port
piston daemon --coverage --mcp-port 5300
```

## Web dashboard

The daemon serves a Blazor WebAssembly dashboard (default `http://localhost:5199`) connected over WebSocket. It shows test results, build errors, engine activity, and MCP tool-call history. The Roslyn diagnostic view has been removed. The dashboard and the HTTP MCP endpoint accept only local host names and refuse cross-origin WebSocket and POST requests from web pages; see [SECURITY.md](SECURITY.md).

A strip under the Tests toolbar describes the latest run: what triggered it (`Calc.cs saved`, `run all`, …), its build time, which tests it ran, and the tests it fixed or newly broke (click to select the first), with the run's elapsed time. Clicking any node in the tree shows its details; double-click or the chevron expands or collapses it. A test's details show its captured output (what the test wrote, e.g. via xUnit's `TestContext.Current.TestOutputHelper`) and link to its project. A failed test's stack trace lists its frames; frames in files under the solution directory are clickable and show a syntax-highlighted excerpt of the source around that line (the first such frame is shown by default), with a whole-file toggle and an **Open in editor** link (`vscode://`). The daemon serves only files inside the solution directory. A project, framework, namespace, class, status or pinned node shows its result counts, failures, slowest tests and (for status and pinned groups) the projects they come from. Every details pane has a **Run** button that builds the affected projects and runs just those tests; it is disabled while another run is in progress. The build is skipped (status `Skipped`) when the last successful build covered those projects, its run finished `Current`, and since then the source fingerprint is unchanged, the watcher has seen no change and the test outputs still exist. A run selects tests by name, so a framework node or a multi-targeted test runs on every framework its project targets, and a theory runs all of its cases. Projects are named by file name, so test projects sharing one (e.g. `One/Tests.csproj` and `Two/Tests.csproj`) show as a single node and run together. A project's details also show the run log of its latest run, one process covering all of its target frameworks: the runner Piston chose (VSTest, MTP server or MTP `dotnet test`), the command line, fallbacks, the process's console output and its exit code. Each log keeps the first 1,500 and last 500 lines of a run.

Project, framework, namespace, class and pinned rows show how many tests they contain, with how many passed (✓) and failed (✗), and every group row shows its tests' summed duration. **Run failed** in the toolbar re-runs every failed test. Click the **Name** or **Duration** column header to sort the tree; click again to reverse direction. Duration starts slowest first and orders groups by summed test time. Pinned tests stay at the top, and status groups keep failures first. When a failure message has an expected and an actual value (xUnit, NUnit, MSTest or Shouldly), the test's details show them side by side with the difference highlighted. With [run history](#run-history-and-failure-classification), a test's details show its recorded results as squares (oldest first) with a duration sparkline and, when it is failing, whether the failure is new, pre-existing or flaky and why; tree rows mark new failures and flaky tests, and the toolbar's **new** and **flaky** toggles show only those tests. The browser tab's title and favicon show the run state (busy, passed or failed), so a background tab still tells you how the last run went, and screen readers announce when a run starts and finishes.

The **Coverage** tab (when the daemon runs with coverage enabled) shows line and branch totals, the measured files grouped by module, folder or as a flat list (filterable; click the *Name* or *Coverage* column header to sort, again to reverse) and the selected file's source with each line's hit count: covered lines green, partly covered branches yellow, uncovered lines red. A file is marked *stale* when it changed since it was measured and *partial* when only a selective run measured it. The line marks are aggregate coverage of the tests that ran. Below the source, a panel lists the tests the impact map records as executing the selected file, each with a badge for how precisely that was measured (`test`, `class` or `run`, see [Coverage and impact selection](#coverage-and-impact-selection)) and an *outdated* marker when the file or the measurement changed since; click a line number to narrow the list to that line, and click a test to open it in the **Tests** tab. After a selective run by changed lines, a test's details say why it ran, and the status bar shows the last run's scope. To export the report as LCOV or Cobertura, use `piston coverage-export` or the `export_coverage` MCP tool.

C# source in the coverage detail pane uses the same theme-aware syntax highlighting as test source excerpts: keywords, strings, comments and numbers are colored without changing the coverage backgrounds or line selection. Other file types are shown as plain text.

The **Runs** tab lists the runs kept in the [run history](#run-history-and-failure-classification), newest first, with each run's trigger, scope and passed, failed and skipped counts. Selecting a run shows its per-test results, failures first with their messages, and compares it with an earlier run (the previous one by default): new failures, fixed tests, tests that passed in both but became at least twice as slow and 100 ms slower, tests still failing, added tests and, for a run of all tests, tests that did not run. Click a test to open it in the **Tests** tab.

Keyboard shortcuts (press `?` in the dashboard for the list): `R` runs the selection, `[` and `]` move to the previous or next failure, `1`–`4` show or hide failed, passed, skipped and not-run tests, `G` cycles the grouping, `E` expands or collapses all, and `S` sorts by duration (press again to reverse).

Dashboard styles are bundled locally; no CDN access is needed. After changing
Tailwind classes in dashboard components, regenerate the checked-in stylesheet:

```sh
cd src/Piston.Web
npm ci
npm run build:css
```

Commit `wwwroot/css/tailwind.css` with the component changes. Normal .NET builds
and tool installations use this generated file and do not require Node.js.

## MCP server (AI agents)

For coding agents, use the stdio server: configure `piston mcp` once, globally, as a local MCP server. Copilot CLI's `~/.copilot/mcp-config.json` would then contain:

```json
{ "mcpServers": { "piston": { "type": "local", "command": "piston", "args": ["mcp"], "tools": ["*"] } } }
```

The agent host starts one `piston mcp` process per session, with the working directory set to the session's worktree. That process:
- runs an in-process engine: a full startup run, then watcher-triggered runs;
- logs only to stderr;
- exits when the session ends (stdin EOF, SIGTERM, or parent death on Unix), killing build/test processes and releasing worktree ownership;
- exits if the worktree is deleted.

If another Piston process already owns the worktree, or the engine cannot start, the server still answers. Every tool then returns an error that ends with ``Use `dotnet test` instead.``. To uninstall, run `dotnet tool uninstall -g Piston` and remove the config entry.

The daemon always exposes an HTTP MCP endpoint backed by the running engine, on port 5200 unless `--mcp-port` (or `mcpPort` in `.piston.json`) says otherwise. Connect at `http://localhost:5200` (the root endpoint, not `/mcp`). The daemon retains current state. Runs launch `dotnet build` and fresh test processes, unless [warm test hosts](#warm-test-hosts) are enabled for MTP projects.

To set up a coding agent, follow [docs/agent-usage.md](docs/agent-usage.md). It covers setup, the verification contract and a custom-instructions snippet.

| Tool | Description |
|---|---|
| `verify` | Get current full verification (optional `expectedSolution` guard; the reply names the `Solution:`). Reuses the latest run if it is a current, full-scope, unfiltered result for the present source fingerprint; otherwise awaits an active full run or forces one, retrying at most twice when the source changes mid-run, then reporting `Stale`. Returns a compact verdict (`Passed`, `TestsFailed`, `BuildFailed`, `Incomplete`, `Stale`, `Cancelled`, `Faulted`, `TimedOut`, `NotFullScope`) with run ID, fingerprint, counts, capped failing tests / build errors and queue info. Only `Passed` is proof. |
| `run_tests` | Request a full build + test run; returns a textual summary plus a `Run:` line with the run ID, status, scope, freshness and source fingerprint. |
| `get_test_run` | Read one run's solution path, provenance, build diagnostics and per-test results as JSON (`piston.test-run/v1`): `runId` = an ID, `latest` (default) or `active`. |
| `get_test_history` | Recorded results of one test (`test`, optional `project`) across runs in this worktree, newest first, with run ID, time, status, duration, source fingerprint and retry attempt; a flaky test has an `Evidence:` line. Empty `test` lists flaky tests with their evidence. See [Run history and failure classification](#run-history-and-failure-classification). |
| `get_test_results` | Read current accumulated test results (may combine several runs; each suite shows its run ID). |
| `set_test_filter` | Apply a test name filter. |
| `get_coverage` | Line and branch coverage as JSON (`piston.coverage/v1`). Without `file`: solution and per-module totals plus the files with the most uncovered lines (`maxFiles`, default 50). With `file`: its totals and its uncovered and partly covered line ranges. Requires coverage. |
| `get_changed_line_coverage` | Coverage of the changed lines (see [git bases](#git-bases)), as JSON (`piston.changed-line-coverage/v1`). Files changed since they were last measured are reported as stale with no line verdicts. Requires coverage. |
| `export_coverage` | Write the coverage report as LCOV or Cobertura (`format`) to `outputPath`, which must be inside the solution directory (default `.piston/coverage/lcov.info` or `cobertura.xml`). Requires coverage. |
| `plan_affected_tests` | Preview what Piston would build and run for `files` (or git changes, see [git bases](#git-bases)) without running anything, as JSON (`piston.affected-plan/v1`): `scope` (`full`, `projects`, `tests` or `none`), an `explanation`, the projects, any `warnings`, and with coverage the selected tests with the changed lines that selected each and the granularity of that evidence (`run`, `class` or `test`). |
| `run_affected_tests` | Run that plan and wait for it: the same JSON plus `run` (ID, status and counts; null when nothing is affected). For example, `base: "branch"` runs the tests affected by everything since the branch left `main`. |
| `clear_results` | Clear current test suites, last-run timestamp, coverage state, and persisted coverage in the current solution directory. A running test run may publish new results afterwards. |

Piston provides no code intelligence (diagnostics, rename, semantic search); agents
can use Roslyn-based tools alongside it.

`run_tests` awaits the specific engine request with timeout/cancellation and
returns that run's record, rather than polling an aggregate phase or reading
results from an earlier run. `get_test_results` reads
accumulated state without starting a run; preserved results from unaffected
suites are not proof that every test ran after the latest edit.

### Run provenance

Every run, including startup and file-change runs, gets a record in the
`piston.test-run/v1` JSON format that `get_test_run` returns. The record holds:

- a unique `runId` and a sequence number;
- the trigger (`Startup`, `Forced`, `FileChange` or `Targeted`) and, for a file-change run, the `changedFiles` that triggered it;
- the status: `Completed`, `Incomplete`, `BuildFailed`, `Cancelled` or `Faulted` (`Queued` or `Running` while active);
- for runs that went through machine-wide admission, an `admission` object (see below);
- the scope (`Full` or `Selective`), `isFiltered` and the effective filter;
- the run context: solution, test projects, build targets, configuration, SDK, runtime and OS;
- run-local `build` output (status, raw MSBuild errors/warnings including diagnostic IDs and locations, duration), or null if the build did not return. A targeted run that reused the last build reports status `Skipped`;
- `fixedTests` (failed before the run, passed in it) and `newlyFailingTests` (failed in the run but not before it, including new tests; empty for the daemon's first run). Each entry carries a `project` suite name and a result `key`, so identically named tests in different projects remain distinct;
- per-project outcomes, duplicate test IDs, and every test's fully qualified name and status. A multi-targeted project reports each test once per framework, with a `targetFramework` field (e.g. `net8.0`); test names in failure lists then carry the framework, as in `Ns.Class.Method [net8.0]`.

When a project reports no tests, its outcome carries a `noTestsReason` and the run's status reason repeats it. Piston checks the project file first (an MTP project without `<OutputType>Exe</OutputType>`, no known test framework, a VSTest project missing `Microsoft.NET.Test.Sdk` or its framework adapter), then the runner output ("No test is available", exit code 8, testhost crashes), and otherwise points at `.piston/diagnostics.log`.

The source fingerprint (`piston-source-v2`) is a SHA-256 over every file under the solution directory except `bin`, `obj`, `TestResults`, `.git`, `.vs`, `.idea`, `.piston` and `node_modules`. It also covers the nearest ancestor `global.json`, `Directory.Build.*`, `Directory.Packages.props` and `NuGet.config`. Paths and contents are length-prefixed (unsigned 64-bit big-endian byte lengths), so embedded NUL bytes cannot collide with file boundaries. Capture is attempted before and after every started run, including cancellation and faults; unavailable fingerprints are reported as `Unknown`. Queued requests cancelled before starting have no fingerprints. A run is `Stale` if the fingerprint changed or the file watcher saw a change during the run; a watcher change also cancels the run.

`isCurrentFullResult` is true only for a `Completed`, unfiltered, `Full`-scope, `Current` run with no duplicate IDs or unfinished (`NotRun`/`Running`) tests. Selective or filtered runs never qualify, and a filter can turn even a forced run into a filtered one.

A `BuildFailed` run can have `Current` build diagnostics but is never a current full
**test** result. Its `build` is captured from that run, not the aggregate `LastBuild`,
and is also available as `TestRunDto.Build` in protocol snapshots. Consumers must
check the requested run ID, solution path, before/after source fingerprints and
freshness before treating a diagnostic as evidence for their source generation.

Limits:

- Full run records (what `get_test_run` returns) are kept for the 16 most recent runs, in memory only. Per-test outcomes persist separately in the [run history](#run-history-and-failure-classification). Execution logs are dropped from older run records; aggregate project suites retain the latest log per project.
- Each test result retains at most 64 Ki characters each for `Output`, `ErrorMessage` and `StackTrace`. Output keeps its beginning and end with a truncation marker; error messages and stack traces keep their beginning. This bounds those three fields to 192 Ki UTF-16 characters (about 384 KiB) per test result, independent of console-output volume. The 16 retained runs still retain their individual test results.
- A build retains at most 500 errors and 500 warnings, with each diagnostic limited to 2,000 characters. `OmittedErrors` and `OmittedWarnings` (also in the MCP `get_test_run` build object) report how many more there were; the dashboard and `verify` report indicate omissions.
- Each test-completion activity event retains at most 50 failures, with the test name and message each limited to 1,000 characters. Each build-completion event retains at most 50 errors of at most 1,000 characters. The event's `failed`/`errorCount` fields remain the total, and the activity timeline shows how many entries were omitted.
- Each project execution log retains at most 2,000 entries of at most 4,000 characters. Older `RecentRuns` records drop their log copies; the latest run and aggregate project suites keep the logs needed by the dashboard.
- `.piston/diagnostics.log` rotates at 10 MiB to one previous file, `.piston/diagnostics.log.1`; log entries are limited to 4,000 characters. Starting the daemon clears both files.
- The fingerprint does not cover sources outside the solution directory, installed SDKs, the NuGet cache, or merged user/machine NuGet configuration.
- `verify` reevaluates project inputs and returns `NotFullScope` for user projects, imports or declared files outside the fingerprint (including excluded directories). SDK/workload files, packages and generated restore imports remain toolchain/cache exclusions. Evaluation failure is `Faulted`; neither verdict is a pass or a hidden fallback.
- Full runs refresh the solution graph before selecting test projects. `verify` also rejects an otherwise current run if its selected projects omit a test project from the refreshed graph.
- The SDK version is recorded from `dotnet --version` run in the solution directory.
- `context.workingDirectory` is the build/SDK-probe directory: the solution directory. `context.testWorkingDirectories` records each test target's actual directory: the solution directory for MTP, the project directory for VSTest. Builds and test processes start there, so the watched solution's `global.json` (SDK and test runner) applies, not the daemon's.

### Worktree ownership and machine execution budget

Daemons for the same OS user coordinate through a per-user directory: `$PISTON_EXECUTION_DIRECTORY` if set (must be absolute), otherwise `Piston/execution` under the local application-data folder (`~/.local/share/Piston/execution` on Linux). It must be on a local filesystem. At startup the daemon creates or tightens its Unix coordination directories to mode 0700 and checks that file locks are enforced there. If they are not (for example `DOTNET_SYSTEM_IO_DISABLEFILELOCKING` is set), the daemon refuses to start.

- **One owner per worktree.** A daemon takes an exclusive lock for its solution's worktree: the nearest ancestor that contains `.git`, after resolving symlinks, or the solution directory if there is none. A second daemon for any solution in the same worktree exits with an error that names the owner's PID, solution and start time. The OS releases the lock when the owner exits or crashes, so a stale owner never blocks a restart.
- **Machine-wide budget.** Before building, each run (startup, forced or file-change) asks for admission. It reserves the daemon's test-process parallelism as CPU slots, plus `execution.memoryMb` if configured. Runs are admitted in strict FIFO order, only while the sum of admitted reservations fits the budget. The budget is read from `budget.json` in that directory on every admission check:

  ```json
  { "cpuSlots": 8, "memoryMb": 16384 }
  ```

  Without the file, the budget is the logical processor count with no memory limit. When the file exists, `cpuSlots` is required and `memoryMb` and `containerSlots` (see [Integration-test resources](#integration-test-resources)) are optional. An unreadable file is an error, not a default budget. Unknown properties, non-positive values, or a reservation that could never fit fail explicitly: at daemon startup, or as a `Faulted` run if the file changes while a run waits. If the budget limits memory, every daemon must set `execution.memoryMb`. MCP text also reports configured memory reservations when memory is unbudgeted, explicitly marked `unbudgeted`.

  Update `budget.json` by writing the complete JSON to a temporary file in the same directory, then atomically renaming it over `budget.json` (without deleting the old file first). This is recommended over editing in place. Empty/partial JSON and I/O failures are retried up to five times at 20 ms intervals (100 ms total delay); persistent failures still report the existing invalid-budget error. Schema errors and access denial fail immediately.
- **Visible queueing.** A waiting run is published as the active run with status `Queued` and an `admission` object: `state` (`Queued`/`Admitted`), `queuedAhead`, `queueLength`, `otherAdmittedRuns`, `cpuSlotsInUse`, `memoryMbInUse`, the reservation, the budget and its source, `queuedAt` and `admittedAt`. It appears in `get_test_run` (`active` or by ID), in the `Active Run:` line of `get_test_results`, and in `ActiveRun`/`LastRun` of the protocol state snapshot. A run superseded while queued ends `Cancelled` without starting, and its replacement keeps the queue position. Leases and tickets are released when a run ends, is cancelled or faults, or when the daemon exits or crashes.

Graceful engine disposal waits for cancelled pipelines to finish and release their admission leases before releasing worktree ownership. Build/test cancellation waits for the killed runner to exit.

Test execution and discovery use a launcher that establishes a Unix process group or Windows job before starting the test command. The launcher uses Piston's runtime installation, but resolves the target command in the original Piston process's context, preserving SDK selection. On Windows, test commands remain windowless and inherit the redirected stdout/stderr handles. After the command exits, redirected stdout/stderr get a five-second drain grace period. If descendants keep those streams open, Piston terminates the owned tree, bounds termination cleanup to ten seconds, and records that output was cut off in the run or diagnostic log.

Limits: this is admission control by reservation. Piston does not cap the actual CPU or memory of builds or test processes (no cgroup or job-object resource limits). A hung but live daemon keeps its lease. Child processes orphaned by a crashed daemon are no longer counted. Daemons of different OS users do not share a budget. Solution loading at startup is not admitted. Owner `.lock` files are left in place on purpose. CI runs the full engine suite, including the execution budget tests, on Linux and Windows. On macOS, the smoke job runs `WorktreeOwnershipTests`, plus the packed-tool daemon and MCP `verify` smoke tests on the samples.

### Integration-test resources

Test projects that start containers or bind fixed ports collide when two worktrees run them at the same time. Piston schedules them through the same per-user coordination directory, so such runs queue instead of failing:

- **Inference.** A test project that directly references a container testing package (`Testcontainers`, `Testcontainers.*`, `DotNet.Testcontainers*`, `Aspire.Hosting.Testing`, `Docker.DotNet*`, `Ductus.FluentDocker*`, `Squadron.*`) uses containers. By default it is isolated per repository: only one run of that project at a time across all worktrees of the repository (on this machine, for this user), because such tests often reuse fixed container names, volumes and ports. Projects without container packages run without coordination unless a rule declares resources.
- **Exclusive resources.** A run holds all of its resources at once, and a project waits, before taking a test-process slot, while another run holds or is queued ahead for any of them. Waiting is first-in, first-out per resource, so nothing starves or deadlocks. Resources are released when the project's test process ends, when the run is cancelled, or when the daemon exits or crashes.
- **Container slots.** `containerSlots` in `budget.json` caps how many container-using test projects run at once machine-wide (for example `{ "cpuSlots": 8, "containerSlots": 2 }`). Without it, container projects are limited only by their exclusive resources.

Declare or relax resources in `.piston.json` with `testResources`. Every rule whose `projects` glob matches a test project applies, in order:

```json
{
  "testResources": [
    { "projects": "tests/Api.IntegrationTests/*.csproj", "exclusive": ["port:5432", "container:api-postgres"] },
    { "projects": "**/*.ContainerTests.csproj", "isolation": "none" },
    { "projects": "Legacy.Tests.csproj", "containers": true, "exclusive": ["resource:shared-sql-database"] }
  ]
}
```

| Property | Description |
|---|---|
| `projects` | Required. Glob matched, ignoring case, against the project path relative to the solution directory with `/` separators. `**` matches any number of directories, `*` and `?` stay within one. A pattern without `/` matches the file name in any directory. |
| `exclusive` | Resources the project's run holds exclusively, shared with any other project (in any worktree) that declares the same key: `port:<1-65535>`, `container:<name>`, or `resource:<any name>`. |
| `containers` | `true` or `false` overrides package inference: whether the project uses containers (and a container slot). |
| `isolation` | `"repository"` (default for container projects) runs the project once at a time across the repository's worktrees; `"none"` drops that default when the project's own resources are declared or it is safe to run in parallel. |

Configuration errors (unknown properties, an invalid key such as `port:70000`, an unknown isolation) fail daemon startup. Rules that match no test project are logged to the diagnostic log. Whole-solution runs, which pass the solution to the runner as one target, hold the union of all test projects' resources.

**Visibility.** A waiting project has status `WaitingForResources`. The protocol state snapshot's `ProjectResourceWaits` and the MCP `get_test_results` text list what it needs and which run holds or is queued ahead for it (resource, project, worktree and PID). The dashboard status bar shows the waiting count with the blockers as a tooltip, and the project's tree node a *waiting for resources* badge. Afterwards, `get_test_run` (each project's `resources`: keys, reasons, `wait` with `waitedMs`, `warnings`) and the run's `ProjectResources` in the protocol record what the project held, and `verify` mentions waits of a second or more.

**Conflict hints.** After acquiring its resources, a run probes its declared `port:` keys and warns when a port is already bound by a process outside Piston. When a failed project's output contains a port or container name conflict (`address already in use`, `port is already allocated`, `container name "…" is already in use`, …), the project's `suspectedResourceConflict` (MCP `get_test_run`, `verify`, `get_test_results`) quotes the evidence and suggests declaring the resource in `testResources`.

Limits: only direct package references are inspected. Coordination covers Piston daemons of the same OS user on one machine; tests run outside Piston, or by another user, are only detected by the port probe and the conflict hints. Test discovery is not gated.

## Coverage and impact selection

Project ownership and transitive dependencies determine affected test projects (Tier 2). With coverage enabled, Tier 3 narrows the run to the tests whose recorded execution touches the **changed lines**:

- Each coverage run records an *impact map*: per test project, the line spans each source file executed, and a snapshot of the file's content. A full, cleanly passing run replaces the project's `run` unit; other runs add to it.
- On a change, Piston diffs the new content against the snapshot and selects the tests of every unit whose executed spans overlap the changed lines. A change outside every executed span (for example a new member) selects every test that executed the file.
- `run` evidence attributes the whole project's execution to all its tests, so it over-selects. While the daemon is idle, the **impact backfill** runs each test method on its own with coverage and records a `test` unit by default (`impactBackfill`: `"class"` for faster but coarser measurements, or `"off"`). Per-test runs are expensive (about 80× one coverage run in the measured sample), so backfill is incremental, cancellable, and only runs while idle. Current finer evidence overrides coarser evidence; an edit makes test units of the changed file outdated, so the `run` unit decides until they are remeasured. Ordinary comments and whitespace edits do not invalidate these measurements or run tests. The status bar shows the backfill's progress, and a new run cancels it.
- Tier 3 abstains, and tests are selected by project, when a test file changed (it may add tests no recording knows), when a changed file has no recording, or when more than 100 tests are selected. Otherwise, a file-level fallback selects the tests whose coverage includes the changed files.

Selection by lines is a heuristic, not a proof: code reached through reflection, static state, configuration or data files can affect a test without appearing in its executed lines, and a forced or full run remains the authority. `plan_affected_tests` previews a selection with the changed lines and evidence granularity behind each test.

Coverage persists in `.piston/piston.db`: the merged line and branch report (what the dashboard, `get_coverage` and the exports read), the file-level test associations and the impact map used for selection. The project graph does not persist; test-result history is stored separately, outside the checkout (see [Run history and failure classification](#run-history-and-failure-classification)). Only files under the solution directory are recorded, so code a test loads from elsewhere (such as fixture projects built in a temp directory) does not count. A full, unfiltered run replaces the report; a selective run adds hits to unchanged files and marks new or changed files it measured as partial. The VSTest runner requests `XPlat Code Coverage` (for example via `coverlet.collector`). The MTP runner passes `--coverage --coverage-output-format cobertura` when the test app was built with `Microsoft.Testing.Extensions.CodeCoverage`, running it through `dotnet test` rather than server mode (so without live per-test progress); without it, the project runs without coverage and its run log says why.

### Git bases

The git-based tools take `base` and `baseRef`:

| `base` | Changes |
| --- | --- |
| `working` (default) | Staged, unstaged and untracked changes against `HEAD`. |
| `branch` (default when `baseRef` is set) | Everything since the branch left `baseRef`, uncommitted and untracked changes included. Without `baseRef`, the base is `origin/HEAD`, else the first of `origin/main`, `origin/master`, `main` and `master` that exists. Without a merge base (a shallow clone), Piston compares with `baseRef` directly and warns. |
| `range` | `baseRef` is `A..B` (A against B), `A...B` (the merge base of A and B against B) or one commit (its own changes). An empty side means `HEAD`. |

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
no implemented `.piston/config.json` or user-global config hierarchy for daemon
options; dashboard preferences live in the separate [user settings file](#user-settings).

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
| `coverageEnabled` | `bool` | Request coverage collection (see `--coverage`). |
| `impactBackfill` | `string` | With coverage, how finely to measure the impact map while idle: `"test"` (default), `"class"` or `"off"`. Per-test backfill is expensive; see [Coverage and impact selection](#coverage-and-impact-selection). |
| `hangThreshold` | `string` | How long a test runs before it is reported as possibly hanging (default `60s`). See [Slow and hanging tests](#slow-and-hanging-tests). |
| `testTimeout` | `string` | Per-test limit; off by default. See [Slow and hanging tests](#slow-and-hanging-tests). |
| `history` | `bool` | Keep per-test run history and classify failures (default `true`). See [Run history and failure classification](#run-history-and-failure-classification). |
| `retryFailures` | `int` | Re-run a run's failed tests up to this many times (`0` to `3`, default `0`: off). See [Retrying failed tests](#retrying-failed-tests). |
| `warmTestHosts` | `bool` | Keep MTP test apps running between runs to skip host start-up (default `false`). See [Warm test hosts](#warm-test-hosts). |
| `parallelism` | `int` | Max concurrent test processes. `0` = auto: half the logical processor count, at least one. |
| `processRecycleAfter` | `int` | Runs after which a pool slot logs a recycling warning (default 50). |
| `pipeName` | `string` | Override the named pipe name. |
| `stdio` | `bool` | Use stdin/stdout JSON-RPC transport. |
| `mcpPort` | `int` | Port for the daemon's MCP server (default 5200). |
| `testExecutionMode` | `string` | `"Auto"` and `"Process"` currently both route between MTP and VSTest. `"InProcess"` is not implemented and throws at startup. |
| `testResources` | `array` | Exclusive resources of test projects and overrides of container inference. See [Integration-test resources](#integration-test-resources). |
| `execution.memoryMb` | `int` | Memory in MiB each run reserves from the machine execution budget (see [Worktree ownership and machine execution budget](#worktree-ownership-and-machine-execution-budget)). Must be positive; required when the budget sets `memoryMb`. A reservation, not a cap. |

### Slow and hanging tests

While tests run, the status bar shows the test that has been running longest and its elapsed time, and
`get_test_results` reports it as `Longest running:`. After `hangThreshold` the test is marked **possibly hanging**,
a warning goes to `.piston/diagnostics.log` once, and the run's `possiblyHangingTests` (in `get_test_run`) lists it.

`testTimeout` is a per-test limit. When a test runs longer, Piston cancels that project's test process (other projects
go on). The test fails with "Timed out after 30 s: the test ran longer than the test timeout (30 s).", the project's
other unfinished tests are `NotRun`, its finished tests keep their results, and the project's outcome is `TimedOut`
with `timedOutTest`. The run ends `Incomplete`, and its status reason names the test.

Durations are seconds (`90`) or a number with a unit (`500ms`, `30s`, `5m`, `1.5h`); `0` or `"off"` turns the setting
off. Tests are timed from when the runner reports them started, so only runners that report running tests (MTP server
mode and VSTest with Piston's live collector) can time them; elsewhere neither setting applies.

### Run history and failure classification

Piston records every finished run's per-test outcomes in a history file outside the checkout, so deleting a worktree
or running `clear_results` keeps it. The file is
`<base>/<repository>-<hash>/<worktree>-<hash>/<solution>.history.json`, where `<base>` is `PISTON_HISTORY_DIRECTORY`
(an absolute path outside the worktree) or `Piston/history` under the per-user local application data folder
(`~/.local/share` on Linux, `%LOCALAPPDATA%` on Windows). Worktrees of one repository share the repository folder and
keep separate files, because branches differ. It keeps the last 20 results of each test, the last 50 runs with their
per-test results and errors, and drops tests not run for 30 days. Only the daemon that owns the worktree writes it.

Each failed test in a run is classified against that worktree's earlier results:

- **new**: the test's previous recorded result was a pass;
- **pre-existing**: it was already failing (the evidence names the first failing run and the last pass, if any);
- **flaky**: within its recorded window it both passed and failed on the same source fingerprint, counting the
  results other worktrees of the same repository recorded on that fingerprint, or it both passed and failed across
  [retries](#retrying-failed-tests) within one run. Only `Current` runs carry a fingerprint, so an edit-break-fix loop
  is not mistaken for flakiness;
- **unknown**: there is no earlier pass or fail. Missing history is not proof that a failure is new.

Every flaky label carries its evidence: how many times the test passed and failed, on which source (or within which
run, for retries), how many of those outcomes came from retries, and which other worktrees contributed, with the
newest outcomes listed. Other worktrees' history files are read, never written.

`verify` lists failures with their class, new ones first, after a `Failure history:` count line; a flaky failure's
tag gives its evidence in brief. `get_test_run` includes `failureClassifications` (project, test, kind, evidence and,
for flaky tests, `flakiness`), and `get_test_history` prints an `Evidence:` line and the other worktrees' outcomes.
The dashboard's **Runs** tab browses and compares the kept runs, and the **Tests** tab marks and filters new failures
and flaky tests; the flaky badge's tooltip and the test's history strip show the evidence. Set `"history": false` to
turn this off.

### Retrying failed tests

With `"retryFailures": n` (1 to 3), a run re-runs its failed tests up to `n` times against the same build, without
coverage, before it finishes; each test stops being retried once it passes. A test that passes on a retry ends
`Passed`, so `verify` can pass, but it is never hidden:

- `verify` lists it under `Passed on retry`, with its last failure message and flakiness evidence, and a test that
  failed every attempt is tagged `(failed all n attempts)`;
- `get_test_run` gives each retried test's earlier `attempts`, the run's `retryLimit`, `summary.passedOnRetry` and
  `retriesSkippedReason`;
- the run history records every attempt (so a pass after a failure marks the test flaky), and `get_test_history`
  marks them `retry n`;
- the dashboard shows a **retried** badge, the attempts in the test's details, retry counts in the **Runs** tab and
  the retry in progress in the status bar.

Retries are skipped, with the reason reported, when more than 20 tests failed (likely a real breakage) and for the
tests of a project that timed out. A retry that crashes, times out, reports no results or fails a test that was not
being retried (such as another case of a parameterized test) is not used, and a retry that does not report a failed
test exactly once (for example without the framework it failed under) does not clear that failure; those tests keep
their failure and the reason is reported too. Retries are off by default.

### Warm test hosts

With `"warmTestHosts": true`, Piston keeps each Microsoft.Testing.Platform test app running in server mode after a
discovery or run, and sends later requests for that app over the same connection, so they skip process start-up and
initialization. Before any build, Piston stops its retained hosts and waits for them to exit, releasing loaded DLLs
so MSBuild can overwrite output on Windows. No-build requests can keep reusing a host. A host is also replaced
when anything in its app's output directory changes, when it exits,
and after a failed, timed-out or cancelled request; cancelling kills it. A host serves one request at a time, so a
second concurrent request for the same app gets its own process, and a reused host that fails before reporting any
test is retried once in a new process. Coverage runs still go through `dotnet test` in a fresh process. Warm hosts
get the environment variable `PISTON_WARM_HOST=1`, so a test suite can tell when process-wide state may outlive a
run; state that a fresh process would reset (statics, environment changes) carries over between runs, which is why
this is off by default. Hosts are asked to exit when the daemon stops.

### User settings

Machine-wide preferences shared by every daemon and dashboard of the current user
live in `$PISTON_SETTINGS_FILE` if set (must be absolute), otherwise
`Piston/settings.json` under the roaming application-data folder
(`~/.config/Piston/settings.json` on Linux, `%APPDATA%\Piston\settings.json` on
Windows). Change them from the gear (Settings) button in the dashboard header,
which also shows the file path. The dashboard writes the file when a preference
changes and reads it each time it connects; you can also edit it by hand. The
color theme is a per-browser preference and is not stored in this file. A missing file, invalid JSON or
invalid values fall back to defaults. Unknown keys are preserved on write.

```json
{
  "dashboard": {
    "detailPane": "bottom"
  }
}
```

| Field | Type | Description |
|---|---|---|
| `dashboard.detailPane` | `string` | Where the test details pane docks when a dashboard opens: `"right"` (default) or `"bottom"`. |

### Child process environment

Builds, test discovery and test hosts get the daemon's environment except for
variables that describe how the daemon itself was started, so that a test sees what `dotnet test`
in a terminal would:

- listen addresses: `ASPNETCORE_URLS`, `ASPNETCORE_HTTP(S)_PORTS`, `ASPNETCORE_HTTPS_PORT`, `DOTNET_URLS`, `DOTNET_HTTP(S)_PORTS`;
- `dotnet watch` and hot reload: `DOTNET_WATCH*`, `DOTNET_HOTRELOAD_*`, `DOTNET_MODIFIABLE_ASSEMBLIES`, and the hot reload entry of `DOTNET_STARTUP_HOOKS` (other hooks are kept);
- launch profiles and IDE debug sessions: `DOTNET_LAUNCH_PROFILE`, `DEBUG_SESSION_*`;
- an Aspire AppHost run: `ASPIRE_*`, `DCP_*`, `DOTNET_DASHBOARD_*` and `DOTNET_RESOURCE_SERVICE_*`. Aspire user settings such as `ASPIRE_CONTAINER_RUNTIME` are kept.

A test run's log lists the variables it did not inherit. To keep some, set
`PISTON_TEST_ENV_PASSTHROUGH` on the daemon to names or `PREFIX*` patterns separated
by `;` or `,` (for example `ASPNETCORE_URLS;ASPIRE_DASHBOARD_*`).

Child processes also get `DiffEngine_Disabled=true` unless it is already set, so snapshot
tests (Verify) do not open a diff tool for every failure while Piston runs in the background.

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

[`build.cs`](build.cs) is a file-based build script (Bullseye) that CI runs: `dotnet run build.cs` builds, tests and
checks formatting; `dotnet run build.cs -- --list-targets` lists the other targets.

Engine-test setup `dotnet restore`/`build` helpers have a 90-second timeout,
drain both output streams, and fail with command, PID, directory, and recent
output diagnostics. On timeout they terminate the
command's whole Unix session or Windows job, including descendants that
outlive their parent. Setup
commands and engine builds disable MSBuild node reuse in their child environment
to prevent persistent nodes retaining redirected output pipes after `dotnet` exits.

### Framework smoke tests

[`samples/Samples.slnx`](samples/README.md) holds an MSTest, NUnit, xUnit v3 and TUnit project, each with one passing,
one deliberately failing and one skipped test. CI packs the tool, installs it and runs two smoke tests on Linux,
Windows and macOS:

- `smoke-daemon` starts `piston daemon` on the samples, waits for the startup run and checks its counts (12 tests:
  4 passed, 4 failed, 4 skipped, across 4 suites).
- `smoke-mcp` starts `piston mcp` on the samples as an MCP client over stdio and calls `verify`. It must report
  `TestsFailed` for a completed full-scope run with the same counts.

To run them locally (with no other Piston process owning this worktree):

```sh
dotnet run build.cs -- smoke
```

### Dogfooding Piston on itself

Run the daemon from a Release build of this repo against `Piston.slnx`:

```sh
dotnet build Piston.slnx -c Release
dotnet src/Piston/bin/Release/net10.0/Piston.dll daemon Piston.slnx
```

The daemon builds the watched solution in the default (Debug) configuration, so
its own builds never overwrite the Release binaries it runs from. Stop it before
rebuilding Release (`dotnet src/Piston/bin/Release/net10.0/Piston.dll stop`), then
start it again. `.piston/diagnostics.log` records what the daemon did (runner choice,
build skips, fallbacks), rotates at 10 MiB and is truncated on each start along with
its single rotated backup. Agents should follow the
[`piston-dashboard` skill](.github/skills/piston-dashboard/SKILL.md), which also
covers opening the dashboard in a Copilot browser canvas.

### AI agent tooling (roslynk)

The repo pins [roslynk](https://www.nuget.org/packages/roslynk) as a local .NET tool and registers it in `.mcp.json`, so Copilot CLI / agent sessions get semantic C# intelligence (diagnostics, find references, rename, code fixes) over `Piston.slnx`. Run `dotnet tool restore` once after cloning.

## Roadmap

Planned work, including a native Copilot canvas for the dashboard and harness
integration assets, is in [docs/ROADMAP.md](docs/ROADMAP.md). See
[ARCHITECTURE.md](ARCHITECTURE.md) for implementation details and limitations.
