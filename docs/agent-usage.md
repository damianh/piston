# Using Piston as a coding agent's test runner

One developer with one or more worktrees, each with its own coding agent session.
Every agent session runs its own `piston mcp` process over stdio, which keeps that
worktree's tests running continuously and answers the agent's MCP tool calls.

## 1. Install the tool

```sh
dotnet pack src/Piston/Piston.csproj -c Release -o packages
dotnet tool install -g Piston --version 0.1.0 --add-source ./packages --no-cache
```

To pick up a rebuilt package, pack with a new version (`-p:Version=0.1.0-dev.2`) and
run `dotnet tool update -g Piston --version 0.1.0-dev.2 --add-source ./packages`.
Alternatively, uninstall first. To uninstall, run `dotnet tool uninstall -g Piston`
and remove the `piston` entry from your MCP config (section 3).

A local install (`--tool-path ./tools`) also works. Then point the MCP `command`
at that path instead of `piston`.

## 2. Configure the worktree (optional)

`piston mcp` resolves the solution from its working directory, the same way the
daemon does: `.piston.json` first, then a single `.sln`/`.slnx`/`.slnf` in the
working directory (the worktree root). If the solution is elsewhere, or there is
more than one, add `.piston.json` to the worktree root:

```json
{
  "solution": "MyApp.slnx"
}
```

`solution` is resolved relative to the file. `piston mcp` ignores `mcpPort`,
`webPort` and `stdio`: it opens no ports and no dashboard.

## 3. Connect the agent

Add one global entry to `~/.copilot/mcp-config.json`, merged with the existing
servers rather than replacing them:

```json
{
  "mcpServers": {
    "piston": {
      "type": "local",
      "command": "piston",
      "args": ["mcp"],
      "tools": ["*"]
    }
  }
}
```

Copilot CLI starts stdio MCP servers with the working directory set to the session's
worktree. Each session therefore gets its own `piston mcp` process, engine and
results. Nothing needs to run before the agent starts. Per process:

- **Startup.** It takes worktree ownership, does a full startup build + test run,
  then reruns on file changes (the same behaviour as the daemon).
- **Output streams.** Logs go to stderr; stdout carries only MCP JSON-RPC.
- **Shutdown.** It exits when the session ends: stdin EOF, SIGTERM, or (on Unix)
  the parent process dying. On exit it cancels runs, kills the build/test process
  trees, and releases ownership. A hard `SIGKILL` cannot clean up; children of an
  in-flight run may then finish on their own.
- **Worktree deleted.** If both the worktree and the solution disappear for about
  5s, it stops the engine. Tools then report the deletion, and the process exits
  a few seconds later.
- **Worktree already owned.** If another Piston process owns this worktree (a
  second session, or a daemon you started), it still serves MCP but starts no
  engine. Every tool then returns `Piston owned by another process (PID …,
  solution …) … Use `dotnet test` instead.`
- **Startup failure.** If there is no solution or no SDK, every tool returns
  `Piston could not start: … Use `dotnet test` instead.`

`verify` and `get_test_run` replies name the solution (`Solution: …` /
`"solutionPath"`), including queued runs and runs cancelled before execution.
`verify` and `run_tests` accept an optional `expectedSolution`
(the solution path, its directory or the worktree root; a parent repository of
a nested worktree does not match). If it does not match, they fail
instead of verifying the wrong tree. Path matching resolves symbolic links and
respects the filesystem's case sensitivity on macOS.

Inspect and clean up from any directory:

```sh
piston status --all     # every live owner (PID, mcp/daemon, solution, worktree) and stale records
piston status           # the daemon or owner for this worktree
piston stop             # stop this worktree's HTTP daemon via its pipe
piston stop --force     # SIGTERM (then kill) the owner if its pipe is unreachable, e.g. a piston mcp process
```

`piston stop` without `--force` never kills an unreachable owner. Instead it prints
the PID, solution and worktree, and exits 1. It still finds owners whose worktree
was deleted (`[worktree missing]`).

### HTTP daemon (humans and the dashboard)

For a dashboard, or to share one engine between a human and an agent, run the HTTP
daemon instead. MCP is always on, at port 5200 by default:

```sh
piston daemon            # add --web-port / --mcp-port if 5199 / 5200 are taken
```

Connect with an entry such as `{"type": "http", "url": "http://localhost:5200/"}`.
The endpoint is the root URL, not `/mcp`. Each daemon serves exactly one
worktree. Do not configure a single global HTTP URL for agents that work in
several worktrees; use `piston mcp` for that.

Daemon startup problems fail fast with `error: ...` and exit code 1. For example:
- another Piston process already owns the worktree;
- a port is already in use.

Piston never edits the agent's configuration and never picks another port. The
daemon shuts down by itself if its worktree is deleted, which frees its ports and
ownership. `piston stop` exits 0 only after the daemon has exited, and prints `No
daemon running` (exit 0) when nothing owns the worktree. While a `piston mcp`
session owns the worktree, the daemon refuses to start.

## 4. The verification contract

Call `verify` (optional `timeoutSeconds`, default 600) whenever you need to know
whether the current code builds and every test passes. It:

1. waits briefly for pending file-change events to settle and fingerprints the
   source tree, then reevaluates the solution's project graph to check that user
   projects, imports and declared file inputs are covered by that fingerprint;
2. reuses the latest run if it is a current, full-scope, unfiltered result for
   exactly that fingerprint and that run also established build-input coverage
   before it built (so inputs outside the fingerprint cannot have differed). No
   build happens and the reply says `Reused: yes`;
3. otherwise awaits an already-running full run, or requests a forced full
   build + test run;
4. if the source changes while that run is in progress (or the run is superseded),
   retries against the new source, at most twice, instead of reporting an old
   result. If the third attempt still has no current result, the verdict is
   `Stale`. The default overall timeout is 10 minutes (`timeoutSeconds`).

The present fingerprint is recaptured even when reusing a result. External
projects (including transitive `ProjectReference`s), linked source files, and
custom imports outside the fingerprint produce `NotFullScope`, not a pass. Declared
file items (for example `Content`/`None`) outside the fingerprint do so even when
the file is missing.
The nearest ancestor build/configuration files remain covered; SDK/workload
installations, restored packages and their generated restore imports remain
toolchain/cache exclusions. If coverage evaluation fails, `verify` reports
`Faulted`. Use `dotnet test` explicitly for unsupported inputs; Piston does not
silently fall back. Edits after a verification reply require a new call.

A file-watcher error (including buffer overflow) makes source monitoring
unreliable. `verify` reports `Faulted` until Piston is restarted; matching
before/after fingerprints cannot prove that no transient edit was missed.

Only `Verdict: Passed` with `Counts as full verification: yes` means the current
source builds and all tests pass. Every other verdict is not a pass:

| Verdict | Meaning | What to do |
|---|---|---|
| `Passed` | Current full run, all tests passed. | Done. |
| `TestsFailed` | Current full run; failing tests are listed (name + message). | Fix them; call `verify` again. |
| `BuildFailed` | The full build of the current source failed; errors listed with ID/file/line. | Fix them; call `verify` again. |
| `Incomplete` | A project crashed, produced no results, or results include duplicate IDs or `NotRun`/`Running` tests. | Inspect `get_test_run`; treat as failure. |
| `Stale` | The source kept changing or runs kept being superseded; no current result after 2 retries. | Stop editing, then call `verify` again. |
| `Cancelled` | Piston is shutting down. | Fall back to `dotnet test` explicitly. |
| `TimedOut` | No result within `timeoutSeconds`. The run may still be queued or running. | Call `verify` again (it awaits the active run) or raise the timeout. |
| `NotFullScope` | A filter is active, or evaluated user build inputs are not fingerprinted. | Clear the filter, or use `dotnet test` explicitly for unsupported inputs. |
| `Faulted` | The engine or pipeline failed. | Fall back to `dotnet test` explicitly and report it. |

An `Admission:` line means the run waited for the machine execution budget (queue
position and wait time). That is expected under load, not a failure.

A `Test resources:` list reports test projects that waited for exclusive test
resources (containers, fixed ports) held by runs in other worktrees, which is
also expected, plus warnings and failures that look like port or container name
conflicts. For a suspected conflict, declare the resource in `testResources` in
`.piston.json` (see the README's *Integration-test resources*) rather than
retrying in a loop.

Do not treat these as verification:
- `get_test_results`: accumulated state that may combine several runs;
- filtered or selective runs;
- a `run_tests` reply read after further edits.

`get_test_run("<runId>")` gives the full JSON record behind any reply.

With `TestsFailed`, a `Failure history:` line counts the failures that are
`new` (the test passed in its previous recorded run in this worktree),
`pre-existing`, `flaky` (passed and failed on the same source, in this or
another worktree of the repository, or across retries in one run) or `unknown`
(no earlier result), and each failed test carries its class, new ones first. A
flaky tag carries its evidence, such as `[flaky: 3 passed/2 failed on
0123456789ab, other worktrees: feature-x]`.
Fix new failures first; they are the ones your change most likely caused.
`get_test_history("<test>")` shows a test's recorded results and the evidence
behind a flaky label. A pre-existing or flaky failure is still a failure: it
does not make the verdict a pass.

When the project sets `retryFailures`, failed tests are re-run within the run. A
test that passes on a retry counts as passed, but `verify` lists it under
`Passed on retry` with its failure message: mention it when you report the
result, because it is flaky.

A tool *error* (not a verdict) is also not a pass. Examples: ownership by another
process, a deleted worktree, a startup failure, or a solution mismatch. On any of
these, run `dotnet test` explicitly and say so. Do the same if the `piston` server
is unavailable. Never report a Piston pass that you did not receive.

## 5. Faster feedback while iterating

Between edits, an agent can run just the tests its change affects instead of the
whole suite. The affected-test tools take `files`, or, without `files`, git
changes (`base`: `working`, `branch` or `range`, with `baseRef`):

- `plan_affected_tests` previews what Piston would build and run, and why;
- `run_affected_tests` runs that plan and waits for it.

With coverage enabled, `get_changed_line_coverage` reports which changed lines
the tests executed. It takes no `files`: it always reads the git changes given
by `base` and `baseRef`.

Selection by changed lines is a heuristic, and an affected-test result does not
count as verification, even when the plan's scope is `full`. Finish with `verify`. See
[Coverage and impact selection](../README.md#coverage-and-impact-selection) for
how tests are selected and for the other tools.

## Custom instructions snippet

Paste into the repository's agent instructions (for example
`.github/copilot-instructions.md`):

```markdown
## Testing with Piston

- The `piston` MCP server (`piston mcp`, one per session) tests this worktree.
- To check whether the code builds and tests pass, call the `verify` tool instead of
  running `dotnet build` / `dotnet test`.
- Only `Verdict: Passed` with `Counts as full verification: yes` counts as passing.
- On `TestsFailed` / `BuildFailed`, fix the listed tests or errors and call
  `verify` again.
- On `Stale`, `Cancelled` or `TimedOut`, call `verify` again; do not report success.
- Do not use `get_test_results` or filtered runs as proof that everything passes,
  and do not set test filters while verifying.
- The `Solution:` line in the reply must be this worktree's solution.
- If the `piston` MCP server is unavailable, a tool returns an error (for example
  "Piston owned by another process" or "could not start"), or `verify` returns
  `Faulted`/`Cancelled`, run `dotnet test` explicitly and say that you fell back
  to it.
```
