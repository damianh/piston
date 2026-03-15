# Piston Test Runner

Continuous test runner for .NET — real-time test feedback, test explorer integration, and inline coverage markers.

## Features

- **Continuous Testing**: Automatically runs tests on file changes, no manual triggers needed.
- **Test Explorer Integration**: Full VS Code Test Explorer with pass/fail/skip status and test navigation.
- **Inline Coverage Markers**: Green/red gutter decorations showing covered and uncovered lines.
- **Status Bar**: Live phase and test count display.
- **Build Errors in Problems Panel**: MSBuild errors shown directly in the Problems view.

## Requirements

- .NET 10 SDK or later
- Piston controller binary on your PATH (or configured via `piston.controllerPath`)
- A `.sln` or `.slnx` solution file in your workspace

## Getting Started

1. Install the extension.
2. Open a .NET solution folder in VS Code.
3. Piston starts automatically and begins watching for changes.
4. View test results in the Test Explorer panel (⌘⇧T / Ctrl+Shift+T).

## Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `piston.controllerPath` | `""` | Path to the `piston` binary. Leave empty to auto-detect from PATH. |
| `piston.solutionPath` | `""` | Override the auto-detected solution path. |
| `piston.args` | `[]` | Extra CLI arguments (e.g. `["--coverage", "--parallelism", "4"]`). |
| `piston.autoStart` | `true` | Auto-start on workspace open. |
| `piston.coverage.enabled` | `true` | Show inline coverage markers. |

## Commands

| Command | Description |
|---------|-------------|
| `Piston: Start` | Start or restart the controller. |
| `Piston: Stop` | Stop the controller. |
| `Piston: Run All Tests` | Trigger an immediate test run. |
| `Piston: Set Test Filter` | Filter tests by name/regex. |
| `Piston: Clear Results` | Clear all test results. |
| `Piston: Toggle Coverage Markers` | Show/hide inline coverage decorations. |
| `Piston: Toggle Output` | Show/hide the Piston output channel. |

## Extension Communication

The extension spawns `piston --headless --stdio <solution>` and communicates via JSON-RPC 2.0 over stdin/stdout (NDJSON framing). This is the same protocol used by the TUI and named-pipe clients.

## License

MIT
