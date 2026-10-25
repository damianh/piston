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
- Twelve tools: `verify`, `run_tests`, `get_test_run`, `get_test_results`,
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

**Run provenance and execution**
- Every run has a run ID, a trigger, a scope (full, selective or filtered),
  before and after source fingerprints (`piston-source-v2`), `Current`/`Stale`
  freshness, its own build diagnostics, and fixed and newly failing tests.
- One owner per worktree through OS file locks, and a machine-wide CPU/memory
  admission budget (`budget.json`) with visible FIFO queueing.
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

**Coverage and selection**
- Solution line and branch coverage stored in `.piston/piston.db`, with LCOV and
  Cobertura export over MCP, HTTP, the protocol and the CLI.
- An impact map with idle per-test (default) or per-class backfill, and
  changed-line test selection with per-test reasons and conservative fallback.
- Git bases (`working`, `branch`, `range`) for affected-test planning and
  changed-line coverage.

**Dashboard**
- Tests tab: a virtualized tree, run strip, failure diffs, a stack-trace source
  view, a history strip and new/flaky filters.
- Runs tab: browse and compare kept runs.
- Coverage tab: a grouped file list, highlighted source with hit counts,
  covering tests per file and line, and backfill progress.
- MCP call history, user settings, keyboard shortcuts and theme-aware styling.

**Distribution and CI**
- A framework-dependent .NET tool (`Piston` package, `piston` command) attached
  to GitHub Releases.
- CI on Linux and Windows, driven by a file-based `build.cs`. Packed-tool smoke
  tests run the MSTest, NUnit, xUnit v3 and TUnit samples through `piston daemon`
  and through `verify` over `piston mcp` on Linux, Windows and macOS.

## Near-term

### Native Copilot canvas
Show the dashboard as a Copilot app canvas rather than a browser tab the agent
opens by hand (today's `piston-dashboard` skill).

- Ship a thin canvas extension that hosts the existing web UI. Do not build a
  second UI.
- Decide how a stdio `piston mcp` session, which opens no ports today, exposes the
  dashboard: an opt-in loopback web port, or attaching to a daemon.
- Validate canvas reload/resume, the disconnected state, actions such as run and
  run failed, theme integration and accessibility.
- Opening the canvas only inspects state. It must not start continuous test
  execution on its own.

### Harness integration assets
- Ship Copilot assets with the tool: an MCP config snippet, a skill or custom
  instructions for the `verify` contract, and the canvas registration.
- Document Claude Code and generic MCP clients.
- Never edit harness configuration without explicit opt-in.

### Distribution
- Publish to NuGet.org: reserve the package ID and use the opt-in release path.
- Validate `dnx` invocation and MCP registry metadata before documenting them.
- Validate tool upgrades while sessions are running, including Windows file locks.

## Later

### Coverage
- A verdict for each changed line: covered by passing tests, covered only by
  failing tests, uncovered, non-executable, or unknown/stale.
- A coverage-aware diff view in the dashboard and canvas, with drill-down to the
  covering tests.
- Branch-level attribution in the impact map.
- Method-level identity, so line shifts do not invalidate evidence. Never reuse old
  line evidence without validated remapping.

### Memory
- A declared baseline: the merge-base with a configured base ref, run in a
  Piston-managed worktree, so "new versus pre-existing" does not depend on this
  worktree's own history.
- Richer flakiness evidence (retries, repeated outcomes across worktrees of one
  repository).

### Execution
- Explore MTP server mode further for structured results and stable test
  identities; keep VSTest/TRX as the compatibility path.
- Integration-test resources: infer likely needs (containers, exclusive
  resources) from package references, with explicit overrides, and schedule
  them within the budget. Address fixed-port and container-name conflicts
  conservatively.
- Per-test output and relevant artifacts in the MCP tools and the dashboard.

### Dashboard
- A session timeline: edits, runs, outcomes, freshness and failure classification.
- A workspace overview: owned worktrees on the machine, queued and running work,
  and budget use.

## Risks and open questions

- Build-output contention with commands run outside Piston (an agent's own
  `dotnet build`).
- Attribution ambiguity when several sessions edit one worktree.
- SDK compatibility, and harness MCP/hook behaviour changes.
- Complete cleanup of descendant processes on every platform.
- The cost of coverage backfill on large solutions, and honest reporting of
  stale or partial evidence.
