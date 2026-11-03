# Piston

Piston keeps .NET test results current while you work. It watches a solution, runs affected tests, and shows results in a dashboard or to an AI coding agent over MCP.

## Requirements

- .NET 10 SDK and ASP.NET Core 10 runtime
- A `.sln`, `.slnx`, or `.slnf` solution, plus its required SDKs and test adapters
- Supported test frameworks include xUnit v2/v3, NUnit, MSTest and TUnit

## Install

When the first package release is available on NuGet.org:

```sh
dotnet tool install --global DamianH.Piston
```

Piston is a framework-dependent .NET tool. Pin a specific release with `--version <version>`.
Releases use calendar versions such as `2026.1101.2`; use `--prerelease` to
include automatically published `-dev` builds. See the [versioning guide](docs/maintainers.md#calendar-versions).

## Get started

From the directory containing your solution, run:

```sh
piston
```

Piston starts or attaches to the test daemon and opens the dashboard at <http://localhost:5199>. To choose a solution explicitly, run `piston path/to/solution.slnx`.

To connect an MCP client, install Piston first, then run the matching setup command:

```sh
piston init copilot --write
# or
piston init claude --write
```

The setup installs the MCP entry and Piston's agent instructions. The client starts a separate stdio server for each worktree. See the [agent setup guide](docs/agent-usage.md) for other clients and configuration.

## Coverage

Coverage is on by default for the daemon and MCP engine. The dashboard shows line and branch coverage
and test impact. To opt out, set `"coverageEnabled": false` in `.piston.json`, or start an engine with:

```sh
piston daemon path/to/solution.slnx --no-coverage
```

Coverage requires a collector such as `coverlet.collector` for VSTest projects or
`Microsoft.Testing.Extensions.CodeCoverage` for Microsoft.Testing.Platform projects. Tests still run without
coverage when a report is unavailable; the dashboard and MCP report incomplete collection. Idle per-test
backfill can be expensive; configure `"impactBackfill": "class"` or `"off"` to reduce its cost.
See the [user guide](docs/user-guide.md#coverage-and-impact-selection).

## Upgrade safely

Stop Piston sessions and daemons before updating the installed tool. On Linux and macOS, a live update can remove files still needed by the running process; on Windows, loaded files can block the update. Close MCP clients, stop each daemon with `piston stop`, then update and restart:

```sh
dotnet tool update --global DamianH.Piston
```

If you installed an earlier local build as `Piston`, uninstall that package
after stopping its processes, then install `DamianH.Piston` instead.
See the [platform-specific upgrade details](docs/user-guide.md#upgrading).

## More information

- [User guide](docs/user-guide.md): commands, dashboard, MCP tools, coverage and configuration
- [Agent setup](docs/agent-usage.md): Copilot, Claude Code and other MCP clients
- [Maintainer publishing guide](docs/maintainers.md): NuGet Trusted Publishing setup
- [Security policy](SECURITY.md)
- [Architecture](ARCHITECTURE.md)
