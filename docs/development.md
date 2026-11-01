# Development

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

[`build.cs`](../build.cs) is a file-based build script (Bullseye) that CI runs: `dotnet run build.cs` builds, tests and
checks formatting; `dotnet run build.cs -- --list-targets` lists the other targets.

Engine-test setup `dotnet restore`/`build` helpers have a 90-second timeout,
drain both output streams, and fail with command, PID, directory, and recent
output diagnostics. On timeout they terminate the
command's whole Unix session or Windows job, including descendants that
outlive their parent. Setup
commands and engine builds disable MSBuild node reuse in their child environment
to prevent persistent nodes retaining redirected output pipes after `dotnet` exits.

### Framework smoke tests

[`samples/Samples.slnx`](../samples/README.md) holds an MSTest, NUnit, xUnit v3 and TUnit project, each with one passing,
one deliberately failing and one skipped test. CI packs the tool, installs it and runs three smoke tests on Linux,
Windows and macOS:

- `smoke-daemon` starts `piston daemon` on the samples, waits for the startup run and checks its counts (12 tests:
  4 passed, 4 failed, 4 skipped, across 4 suites).
- `smoke-mcp` starts `piston mcp` on the samples as an MCP client over stdio and calls `verify`. It must report
  `TestsFailed` for a completed full-scope run with the same counts.
- `smoke-init` runs `piston init copilot --write` and `piston init claude --write --scope repo` against temporary
  locations, checks the installed files, and checks that a second run changes nothing.

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
[`piston-dashboard` skill](../.github/skills/piston-dashboard/SKILL.md), which also
covers opening the native Piston canvas, with a browser fallback.

### AI agent tooling (roslynk)

The repo pins [roslynk](https://www.nuget.org/packages/roslynk) as a local .NET tool and registers it in `.mcp.json`, so Copilot CLI / agent sessions get semantic C# intelligence (diagnostics, find references, rename, code fixes) over `Piston.slnx`. Run `dotnet tool restore` once after cloning.

## Roadmap

Planned work is in [docs/ROADMAP.md](ROADMAP.md). See
[ARCHITECTURE.md](../ARCHITECTURE.md) for implementation details and limitations.
