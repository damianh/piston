---
name: piston
description: Verify .NET code with the Piston MCP server instead of running dotnet build or dotnet test. Use whenever you need to know whether the current worktree builds and all of its tests pass, before reporting a change as done, or when iterating on failing tests.
---

# Verifying .NET changes with Piston

The `piston` MCP server (`piston mcp`, one process per agent session) keeps this
worktree's solution built and tested continuously. Use its tools instead of
running `dotnet build` / `dotnet test` yourself.

## The verification contract

Call `verify` (optional `timeoutSeconds`, default 600) whenever you need to know
whether the current code builds and every test passes. It reuses a current full
result for the present source or runs a full build and test run, and retries if
the source changes underneath it.

Only `Verdict: Passed` with `Counts as full verification: yes` means the current
source builds and all tests pass. Every other verdict is not a pass:

| Verdict | What to do |
|---|---|
| `Passed` | Done. Mention any tests listed under `Passed on retry`: they are flaky. |
| `TestsFailed` | Fix the listed tests (new failures first), then call `verify` again. |
| `BuildFailed` | Fix the listed errors, then call `verify` again. |
| `Incomplete` | Inspect `get_test_run`; treat it as a failure. |
| `Stale` | Stop editing, then call `verify` again. |
| `TimedOut` | Call `verify` again (it awaits the active run) or raise `timeoutSeconds`. |
| `NotFullScope` | Clear any test filter, or run `dotnet test` explicitly for inputs Piston cannot fingerprint. |
| `Cancelled` | Piston is shutting down: run `dotnet test` explicitly. |
| `Faulted` | Run `dotnet test` explicitly and report the fault. |

Rules:

- The `Solution:` line in the reply must be this worktree's solution. Pass
  `expectedSolution` to `verify` when the worktree contains more than one.
- A `pre-existing` or `flaky` failure is still a failure.
- `Admission:` and `Test resources:` lines describe waiting for shared machine
  resources. They are expected under load, not failures.
- Edits made after a `verify` reply need a new `verify` call.

## What is not verification

- `get_test_results`: accumulated state that may combine several runs, or
  results restored from an earlier session.
- Filtered or selective runs, including `run_affected_tests`, even when its plan
  scope is `full`.
- A `run_tests` reply read after further edits.
- Changed-line coverage verdicts: they describe test reach, not correctness.

## Faster feedback while iterating

Between edits, `plan_affected_tests` previews and `run_affected_tests` runs only
the tests your change affects (from `files`, or git changes via `base` and
`baseRef`). Finish with `verify`.

`get_test_run("<runId>")` returns the full record behind any reply, and
`get_test_history("<test>")` shows a test's recorded results.

## When Piston is unavailable

If the `piston` MCP server is not connected, or a tool returns an error instead
of a verdict (for example "Piston owned by another process", "could not start",
a deleted worktree or a solution mismatch), run `dotnet test` explicitly and say
that you fell back to it. Never report a Piston pass that you did not receive.

## Dashboard

If the `piston-dashboard` canvas is installed, opening it or its `refresh` action
only inspects state; `run` and `run_failed` request tests. A `piston mcp`
session serves the dashboard only when started with `--dashboard` (or
`"dashboard": true` in `.piston.json`).
