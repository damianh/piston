# Copilot instructions

## Setup

- Run `dotnet tool restore` before starting work; it installs the pinned local tools (including `roslynk`) from `dotnet-tools.json`.
- The solution file is `Piston.slnx`. Build with `dotnet build Piston.slnx`, test with `dotnet test Piston.slnx`.
- Tests use xUnit v3 on Microsoft.Testing.Platform (`xunit.v3.core.mtp-v2`; `global.json` sets the MTP test runner). Write test output via `TestContext.Current.TestOutputHelper`, and in test classes that need cancellation declare `private readonly CancellationToken _ct = TestContext.Current.CancellationToken;`.

## Dogfooding the daemon and dashboard

To run Piston against this repo or view its dashboard, follow the `piston-dashboard` skill (`.github/skills/piston-dashboard/SKILL.md`): run the daemon from the Release build, detached, and open `http://localhost:5199` in the browser canvas rather than letting `piston` launch a system browser.

## roslynk MCP server

The `roslynk` MCP server (configured in `.mcp.json`) provides Roslyn-based semantic intelligence for C#. Prefer it over plain text search and manual edits when working on C# code:

- Call `open_solution` with `Piston.slnx` at the start of a session (check `get_solution_status` first), and `reload_solution` after adding/removing projects or files outside roslynk.
- Navigation: use `search_symbols`, `find_definition`, `find_references`, `find_implementations`, `get_callers`/`get_callees`, and `get_type_hierarchy` instead of grep for symbol lookups.
- Refactoring: use `rename_symbol`, `rename_parameter`, `change_signature`, `extract_method`, and `remove_unused_usings` so all usages are updated consistently.
- Diagnostics: use `get_diagnostics` to verify changes compile cleanly; use `get_code_actions` / `apply_code_fix` for analyzer-suggested fixes.
- Cleanup: `find_dead_code` and `find_dead_conditionals` help identify unused code.

Still run `dotnet build` / `dotnet test` to validate changes before finishing.
