# Piston roadmap

## Direction

Piston is an MCP-based continuous test runner for .NET. A coding agent connects
to it over MCP, normally one `piston mcp` process per session and worktree. It
keeps that worktree's tests running as files change and answers with results
tied to the source they ran against. People use the same engine through the web
dashboard, which is an equal surface rather than an afterthought.

Its value comes from three things:

- **Trustworthy answers:** `verify` reports a pass only for a current, full-scope
  result; everything else is labelled stale, partial or failed.
- **Memory:** per-test history that separates new failures from pre-existing
  and flaky ones.
- **Targeting:** running only the tests a change affects, down to changed lines
  when coverage is enabled.

Piston does not assign work to agents, coordinate their conversations or provide
code intelligence; agents combine it with their own Roslyn tools. It is not an
IDE test-runner extension.

This document lists shipped capabilities and planned work. README and
ARCHITECTURE describe the implementation in detail.

## Shipped

**Agent interface (MCP)**
- `piston mcp`: a per-session stdio MCP server with an in-process engine. The
  session's working directory selects the worktree. The server exits with the
  session or when the worktree is deleted, and fails safe ("use `dotnet test`")
  when another process owns the worktree or the engine cannot start.
- The daemon's HTTP MCP endpoint, sharing one engine with the dashboard.
- Thirteen tools: `verify`, `run_tests`, `get_test_run`, `get_test_result`, `get_test_results`,
  `get_test_history`, `set_test_filter`, `clear_results`, `get_coverage`,
  `get_changed_line_coverage`, `export_coverage`, `plan_affected_tests` and
  `run_affected_tests`.
- The `verify` contract: it reuses, awaits or forces a full run for the present
  source fingerprint and retries when edits supersede a run. Verdicts are
  `Passed`, `TestsFailed`, `BuildFailed`, `Incomplete`, `Stale`, `NotFullScope`
  and others. An `expectedSolution` guard checks that the right tree was verified,
  and build inputs outside the fingerprint are reported as not covered.
- An agent setup guide and custom-instructions snippet
  ([agent-usage](agent-usage.md)).
- `get_test_result` retrieves one test's separate output streams, failure details
  and attachment metadata; the dashboard shows the same streams and serves
  retained, size-capped artifacts.

**Run provenance and execution**
- Every run has a run ID, a trigger, a scope (full, selective or filtered),
  before and after source fingerprints (`piston-source-v2`), `Current`/`Stale`
  freshness, its own build diagnostics, and fixed and newly failing tests.
- One owner per worktree through OS file locks, and a machine-wide CPU/memory
  admission budget (`budget.json`) with visible FIFO queueing.
- Integration-test resource scheduling: container use inferred from package
  references, per-repository isolation of container test projects, exclusive
  `port:`/`container:`/`resource:` keys and overrides in `testResources`, an
  optional `containerSlots` budget, visible waits, and port-probe and
  conflict-signature hints.
- Correct selective builds: one solution-filter build of the affected projects,
  then no-build test execution.
- VSTest and Microsoft.Testing.Platform routing, with opt-in warm MTP test hosts.
- Slow and possibly hanging test reporting, and an optional per-test timeout.
- A sanitized child-process environment and process-tree cleanup on Unix and
  Windows.

**Memory**
- Persistent per-worktree run history outside the checkout, grouped by repository.
- Failures classified as new, pre-existing, flaky (same fingerprint, both
  outcomes) or unknown, in `verify`, `get_test_run` and `get_test_history`.
- Flakiness evidence on every flaky label: counts, source, outcomes from other
  worktrees of the repository, and opt-in in-run retries of failed tests
  (`retryFailures`), reported in MCP and the dashboard.

**Coverage and selection**
- Solution line and branch coverage stored in `.piston/piston.db`, with LCOV and
  Cobertura export over MCP, HTTP, the protocol and the CLI.
- An impact map with idle per-test (default) or per-class backfill, and
  changed-line test selection with per-test reasons and conservative fallback.
- Branch-level attribution in impact-map reasons and changed-line coverage:
  per-unit, per-line outcome counts (Cobertura does not identify individual
  outcomes). Backfill re-measures existing units missing branch evidence.
- Method-level identity for C# coverage evidence: line shifts preserve validated
  member evidence, while edited members are invalidated and unvalidated lines are
  never reused.
- Git bases (`working`, `branch`, `range`) for affected-test planning and
  changed-line coverage.
- A verdict for each changed line: covered by passing tests, covered only by
  failing tests, uncovered, non-executable, or unknown with a reason (stale, not
  measured, no or mixed test evidence), over MCP and the protocol.

**Dashboard**
- Tests tab: a virtualized tree, run strip, failure diffs, a stack-trace source
  view, a history strip and new/flaky filters.
- Runs tab: browse and compare kept runs.
- Timeline tab: the daemon session's edits and runs with their outcomes,
  freshness and failure classification, tracing each failing test to the run
  and edit that broke it.
- Coverage tab: a grouped file list, highlighted source with hit counts,
  covering tests per file and line, and backfill progress; a Changes mode shows
  the git diff with each changed line's verdict and drills down to its covering
  tests, also from the Copilot canvas.
- MCP call history, user settings, keyboard shortcuts and theme-aware styling.
- Machine tab: owned worktrees and dashboard links, budget use, running work and
  the FIFO queue; `piston status --all` prints the same machine overview.

**Distribution and CI**
- A framework-dependent .NET tool (`Piston` package, `piston` command) attached
  to GitHub Releases.
- CI on Linux and Windows, driven by a file-based `build.cs`. Packed-tool smoke
  tests run the MSTest, NUnit, xUnit v3 and TUnit samples through `piston daemon`
  and through `verify` over `piston mcp`, and install the harness assets with
  `piston init`, on Linux, Windows and macOS.

**Project-scoped Copilot canvas**
- A thin `.github/extensions/piston-dashboard/` extension hosts the existing
  web UI with an independent connection shell and worktree-owner discovery.
- Stdio MCP can opt in to an ephemeral loopback dashboard with `--dashboard`
  or `.piston.json`'s `dashboard: true`; no daemon attachment or second engine.
- Inspection-only open/refresh/reconnect, explicit run/run-failed actions,
  disconnected recovery, shared failed-test selection and host-following themes.

**Harness integration assets**
- `piston init copilot|claude` prints the MCP entry, the `piston` skill (the
  `verify` contract) and, for Copilot, the canvas extension, all embedded in the
  tool. It installs them only with `--write`, at user or repository scope.
- Merges only the `piston` MCP entry; content that differs is kept unless `--force`.
  Claude Code's user config is never edited; the `claude mcp add` command is printed.
- Claude Code and generic MCP clients are documented in `docs/agent-usage.md`.

## Near-term

### Distribution
- Publish to NuGet.org: reserve the package ID and use the opt-in release path.
- Validate `dnx` invocation and MCP registry metadata before documenting them.
- Validate tool upgrades while sessions are running, including Windows file locks.

## Later

### Execution
- Explore MTP server mode further for structured results and stable test
  identities; keep VSTest/TRX as the compatibility path. The investigation and
  current decision are in [MTP server-mode investigation](mtp-server-mode.md).

## Risks and open questions

- Build-output contention with commands run outside Piston (an agent's own
  `dotnet build`).
- Attribution ambiguity when several sessions edit one worktree.
- SDK compatibility, and harness MCP/hook behaviour changes.
- Complete cleanup of descendant processes on every platform.
- The cost of coverage backfill on large solutions, and honest reporting of
  stale or partial evidence.
