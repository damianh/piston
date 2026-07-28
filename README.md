# Piston

An AI-native continuous test runner and code intelligence server for .NET. Think NCrunch and ReSharper, rebuilt as a headless daemon that serves humans (web dashboard, IDE extensions) and AI agents (MCP) alike.

Piston watches a .NET solution for file changes, determines what was impacted, rebuilds, runs the affected tests in parallel, and streams live results to every connected client. A supervised Roslyn workspace exposes diagnostics, semantic search, AST inspection, and refactorings over the same daemon.

## Requirements

- .NET 10 SDK
- A .NET solution with xUnit, NUnit, or MSTest tests

## Desktop app (trial)

Piston ships as a cross-platform desktop app: a native window hosting the
dashboard, with the engine running in-process. Download the installer for your
platform from [GitHub Releases](https://github.com/damianh/piston/releases):

| Platform | Package |
|---|---|
| Windows x64 / arm64 | `Piston-win-x64-Setup.exe` / `Piston-win-arm64-Setup.exe` |
| macOS x64 / arm64 (Apple Silicon) | `Piston-osx-x64.pkg` / `Piston-osx-arm64.pkg` |
| Linux x64 / arm64 | `Piston-linux-x64.AppImage` / `Piston-linux-arm64.AppImage` |

On first launch, pick a solution (or pass one as the first argument); Piston
remembers it for next time. Closing the window on Windows minimizes to the
tray; the tray menu has *Open Dashboard*, *Start on login*, and *Quit*.
Trial builds auto-update in the background from GitHub Releases and apply the
update on exit.

**Trial limitations:**

- Builds are unsigned: expect a SmartScreen warning on Windows ("More info" >
  "Run anyway") and a Gatekeeper prompt on macOS (right-click > Open, or
  `xattr -d com.apple.quarantine <app>`).
- Linux needs WebKitGTK: `sudo apt install libwebkit2gtk-4.1-0` (or your
  distro's equivalent).
- Tray icon and close-to-tray are Windows-only for now; on macOS/Linux,
  closing the window quits the app.
- The .NET 10 SDK must still be installed: the engine shells out to `dotnet`
  to build and run your tests.

## Usage

```sh
# Start (or attach to) the daemon and open the web dashboard
piston [<solution>] [--pipe-name <name>] [--web-port <port>]

# Run the daemon in the foreground
piston daemon [<solution>] [options]

# Manage a running daemon
piston stop [<solution>]
piston status [<solution>]
```

`<solution>` is a path to a `.sln`, `.slnx`, or `.slnf` file; if omitted, Piston auto-discovers the first solution in the current directory.

**`piston daemon` options:**

| Option | Description |
|---|---|
| `--debounce <ms>` | File-change debounce interval in milliseconds. |
| `--filter <pattern>` | Substring or regex to filter test names on startup. |
| `--coverage` | Enable code coverage collection during test runs. |
| `--parallelism <n>` | Max concurrent test processes. `0` = auto. |
| `--stdio` | Use stdin/stdout for JSON-RPC transport (for IDE extensions). |
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

The daemon serves a Blazor WebAssembly dashboard (default `http://localhost:5199`) connected over WebSocket. It shows live test results, build errors, Roslyn diagnostics, engine activity, and MCP tool-call history.

## MCP server (AI agents)

With `--mcp-port` (or `mcpPort` in `.piston.json`), the daemon exposes an MCP endpoint that gives agents an always-warm view of the solution — no cold `dotnet build`/`dotnet test` cycles.

| Tool | Description |
|---|---|
| `RunTests` | Trigger a build + test run. |
| `GetTestResults` | Read current test results. |
| `SetTestFilter` | Apply a test name filter. |
| `ClearResults` | Clear accumulated results. |
| `LoadWorkspace` | Load the Roslyn workspace. |
| `GetDiagnostics` | Compiler errors/warnings per project. |
| `SemanticSearch` | Find all references to a symbol. |
| `GetAst` | Inspect declaration-level syntax trees. |
| `Rename` | Rename a symbol solution-wide (preview or apply). |
| `NotifyFileChanged` | Sync an edited file into the workspace. |

## VSCode extension

The `extensions/vscode` folder contains an extension that connects to the daemon over stdio JSON-RPC, providing a test explorer and coverage gutter markers.

## Configuration file

Piston reads an optional `.piston.json` in the solution directory. CLI flags take precedence over config file values.

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
| `solution` | `string` | Path to the solution file (relative to the config file). |
| `debounceMs` | `int` | File-change debounce interval in milliseconds. |
| `testFilter` | `string` | Default test filter (regex or substring). |
| `coverageEnabled` | `bool` | Enable code coverage collection. |
| `parallelism` | `int` | Max concurrent test processes. `0` = auto. |
| `processRecycleAfter` | `int` | Runs after which a pool slot logs a recycling warning (default 50). |
| `pipeName` | `string` | Override the named pipe name. |
| `stdio` | `bool` | Use stdin/stdout JSON-RPC transport. |
| `mcpPort` | `int` | Enable the MCP server on this port. |
| `testExecutionMode` | `string` | `"Auto"`, `"Process"`, or `"InProcess"`. |

## Building from source

```sh
git clone <repo>
cd piston
dotnet build Piston.slnx

# CLI
dotnet run --project src/Piston -- [args]

# Desktop app
dotnet run --project src/Piston.Desktop -- [solution]
```

Run tests:

```sh
dotnet test Piston.slnx
```

See [ARCHITECTURE.md](ARCHITECTURE.md) for the full system design.
