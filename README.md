# Piston

An agent-facing continuous test backend for .NET, with a headless daemon, HTTP MCP test tools, and a web dashboard.

Piston watches a .NET solution, selects affected test projects, builds, runs test projects with bounded concurrency, and publishes results to connected clients. It uses standard `dotnet build` and `dotnet test` in the watched checkout.

**Phase 0 status:** build correctness and [surface reduction/tool packaging (#4)](https://github.com/damianh/piston/pull/4) are pending companion changes. They rebuild affected test outputs before execution and remove the Roslyn code-intelligence backend, diagnostic polling/UI, and VSCode extension. This documentation change does not ship those implementation changes. Desktop source is to be frozen; .NET tool packaging replaces the desktop installer release workflow.

## Requirements

- .NET 10 SDK
- A `.sln`, `.slnx`, or `.slnf` solution with compatible test projects (for example xUnit, NUnit, or MSTest)
- The SDK and test adapters/extensions required by the target solution; MTP projects need the appropriate `global.json` test-runner configuration

## Delivery

The primary surface is the CLI daemon and its test APIs. Until the companion
tool-packaging change lands, use the source commands below. Historical desktop
trial installers are not the forward delivery path; the existing Photino desktop
source remains for reference rather than ongoing feature development.

**After the packaging companion lands**, build and install the local .NET tool:

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

The companion release workflow attaches a `.nupkg` to GitHub Releases rather
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

`<solution>` is a path to a `.sln`, `.slnx`, or `.slnf` file. An explicit argument wins; otherwise Piston uses the `solution` field in the current directory's `.piston.json`, resolved relative to that file. Without a configured path, exactly one solution must exist in the current directory; multiple candidates require an explicit or configured path. A missing or invalid configured solution is an error, not a discovery fallback.

**`piston daemon` options:**

| Option | Description |
|---|---|
| `--debounce <ms>` | File-change debounce interval in milliseconds. |
| `--filter <pattern>` | Test filter passed to the selected test runner on startup; syntax depends on the runner. |
| `--coverage` | Request coverage collection on the VSTest path; requires a compatible collector in the test project. |
| `--parallelism <n>` | Max concurrent test processes. `0` = auto. |
| `--stdio` | Use stdin/stdout for engine JSON-RPC transport, not MCP. |
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

The daemon serves a Blazor WebAssembly dashboard (default `http://localhost:5199`) connected over WebSocket. It shows test results, build errors, engine activity, and MCP tool-call history. The Roslyn diagnostic view is removed by the Phase 0 companion change.

## MCP server (AI agents)

With `--mcp-port` (or `mcpPort` in `.piston.json`), the daemon exposes an HTTP MCP endpoint backed by the running engine. For `--mcp-port 5200`, connect at `http://localhost:5200` (the root endpoint, not `/mcp`). The daemon retains current state, but runs still launch `dotnet build` and fresh `dotnet test` processes; there is no prewarmed test-process pool.

| Tool | Description |
|---|---|
| `run_tests` | Request a full build + test run and a textual summary. |
| `get_test_results` | Read current test results. |
| `set_test_filter` | Apply a test name filter. |
| `clear_results` | Clear current test suites, last-run timestamp, coverage state, and persisted coverage in the current solution directory. A running test run may publish new results afterwards. |

These are the retained Phase 0 tools. The companion surface-reduction change removes `LoadWorkspace`, `GetDiagnostics`, `SemanticSearch`, `GetAst`, `Rename`, and `NotifyFileChanged`; Piston will not own Roslyn workspace synchronization.

The packaging companion also corrects `run_tests` completion handling: it awaits
the engine run with timeout/cancellation instead of polling for a phase that
normal completion never reaches. That fix is not included in this
documentation-only change. `get_test_results` reads
accumulated state without starting a run; preserved results from unaffected
suites are not proof that every test ran after the latest edit.

## Coverage and impact selection

Project ownership and transitive dependencies determine affected test projects. The optional Tier 3 path adds a file-based test-FQN filter when usable coverage exists. It is **not verified per-test/changed-line selection**: `CoverageProcessor` unions covered lines across reports and attributes them to every test returned in that run. The analyzer queries whole files, not changed line ranges.

Coverage associations persist in `.piston/piston.db`; test-result history and the project graph do not. The VSTest runner requests `XPlat Code Coverage` (for example via `coverlet.collector`). The current MTP runner does not request or return coverage reports. Fine-grained attribution and selection correctness are future experiments.

### Pending selective-build correction

The build-correctness companion builds the union of affected production and
test projects with one temporary solution-filter build, allowing restore and
MSBuild dependency scheduling before no-build test execution. It preserves
`.sln`/`.slnx` solution context and `.slnf` membership. Unsupported/unreadable
solution paths or non-member targets fall back to sequential per-project builds
with `--no-restore`; a selective run with no affected tests builds only. Full
runs still build the original solution. This is pending implementation, not a
change made by this documentation PR.

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

## Building from source

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

Fine-grained coverage, a measurement harness, Copilot canvas/harness integrations,
and a per-user hub with isolated worktree workers are future work, not existing
features. Aspire is one possible generic integration-test example, not a
specialized backend. See [ARCHITECTURE.md](ARCHITECTURE.md) for implementation
details and limitations.
