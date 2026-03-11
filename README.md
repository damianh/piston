# Piston

A .NET 10 TUI continuous test runner. Think NCrunch, but in your terminal.

Piston watches a .NET solution for file changes, rebuilds automatically, runs tests, and displays live results in a keyboard-driven terminal UI.

## Requirements

- .NET 10 SDK
- A .NET solution with xUnit, NUnit, or MSTest tests

## Usage

```
piston [<solution>] [--debounce <ms>] [--filter <pattern>]
```

**Arguments:**

| Argument | Description |
|---|---|
| `<solution>` | Path to `.sln` or `.slnx` file. Auto-discovers if omitted. |
| `--debounce <ms>` | File-change debounce interval (default: 300ms). |
| `--filter <pattern>` | Regex or substring to filter test names on startup. |

**Examples:**

```sh
# Auto-discover solution in current directory
piston

# Explicit solution path
piston ./src/MyApp.slnx

# Start with a filter pre-applied
piston --filter "CustomerTests"
```

## Keyboard shortcuts

| Key | Action |
|---|---|
| `R` | Force re-run (rebuild + test) |
| `F` | Open filter prompt |
| `C` | Clear test results |
| `G` | Cycle grouping mode (Project/NS/Class → By Status → Flat) |
| `E` | Expand / collapse all tree nodes |
| `P` | Pin / unpin selected test (pinned tests stay at top) |
| `1` | Toggle visibility of Passed tests |
| `2` | Toggle visibility of Failed tests |
| `3` | Toggle visibility of Skipped tests |
| `4` | Toggle visibility of Not Run tests |
| `]` | Jump to next failing test |
| `[` | Jump to previous failing test |
| `Q` / `Ctrl+C` | Quit |

## Configuration file

Piston reads an optional `.piston.json` in the solution directory. CLI flags take precedence over config file values.

```json
{
  "debounceMs": 500,
  "testFilter": "MyNamespace"
}
```

| Field | Type | Description |
|---|---|---|
| `debounceMs` | `int` | File-change debounce interval in milliseconds. |
| `testFilter` | `string` | Default test filter (regex or substring). |

## TUI layout

```
┌──────────────────────────────────────────────────────┐
│ ● WATCHING   MyApp.slnx          Last run: 14:32:01  │
├───────────────────┬──────────────────────────────────┤
│ MyApp.Tests       │ MyApp.Tests.CustomerService       │
│   CustomerService │                                   │
│     ✓ CanCreate   │ Status:   PASSED                  │
│     ✗ CanDelete   │ Duration: 42ms                    │
│   OrderService    │                                   │
│     ✓ CanPlace    │                                   │
├───────────────────┴──────────────────────────────────┤
│ ✓ 2  ✗ 1  ○ 0   14:32:01  [F] filter  [R] run  [Q]  │
└──────────────────────────────────────────────────────┘
```

When the build fails, the detail panel shows the compiler error messages.

## Building from source

```sh
git clone <repo>
cd piston
dotnet build Piston.slnx
dotnet run --project src/Piston -- [args]
```

Run tests:

```sh
dotnet test Piston.slnx
```
