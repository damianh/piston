---
name: piston
description: Verify .NET code with the Piston MCP server instead of running dotnet build or dotnet test. Use whenever you need to know whether the current worktree builds and all of its tests pass, before reporting a change as done, or when iterating on failing tests.
---

# Verifying .NET changes with Piston

The `piston` MCP server (`piston mcp`, one process per agent session) keeps this
worktree's solution built and tested continuously. Use its tools instead of
running `dotnet build` / `dotnet test` yourself. Starting a Piston MCP engine
itself triggers a full build and test run and subsequent runs on changes.

Invoking `/piston` alone does not specify an operation: ask what the user wants.
Do not treat a solution path alone as a request to run tests.

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
| `NotFullScope` | Clear any test filter; otherwise report that verification is incomplete. |
| `Cancelled` | Report that Piston is shutting down and verification is incomplete. |
| `Faulted` | Report the fault and that verification is incomplete. |

Rules:

- The `Solution:` line in the reply must be this worktree's solution. Pass
  `expectedSolution` to `verify` when the worktree contains more than one. This
  is a guard, not a way to select the server's solution; configure the server
  with a solution path or `.piston.json` and restart it if necessary.
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
a deleted worktree or a solution mismatch), report the error and stop. Do not
run `dotnet test` or another test command as a fallback without the user's
explicit consent. Never report a Piston pass that you did not receive.

## Dashboard

If the `piston-dashboard` canvas is installed, opening it or its `refresh` action
only inspects state. Its Start Piston button starts a daemon and its startup run;
`run` and `run_failed` request tests. A daemon owns the worktree and cannot share
it with a separate stdio MCP engine. A `piston mcp`
session serves the dashboard on an ephemeral loopback port.
