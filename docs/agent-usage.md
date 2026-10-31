# Using Piston as a coding agent's test runner

One developer with one or more worktrees, each with its own coding agent session.
Every agent session runs its own `piston mcp` process over stdio, which keeps that
worktree's tests running continuously and answers the agent's MCP tool calls.

## 1. Install the tool

```sh
dotnet tool install --global Piston
```

This requires an actual published NuGet release. Until one is available, or for
local development, pack with `dotnet pack src/Piston/Piston.csproj -c Release -o packages`
and install with `--version 0.1.0 --source ./packages --no-cache`.
To pick up a rebuilt package, use a new version (`-p:Version=0.1.0-dev.2`).
**Disconnect all stdio MCP sessions and stop daemons before updating on every OS**,
then run `dotnet tool update --global Piston` (for a local package, add
`--version 0.1.0-dev.2 --source ./packages`) and reconnect the clients.
See the [upgrade behavior and publishing prerequisites](../README.md#upgrading).
To uninstall, run `dotnet tool uninstall -g Piston`
and remove the `piston` entry from your MCP config, plus any `skills/piston` and
`extensions/piston-dashboard` folders that `piston init` installed (section 3).

A local install (`--tool-path ./tools`) also works. Then point the MCP `command`
at that path instead of `piston`.

Alternatively, after publication a client can acquire a pinned version with
`dnx`. In the MCP entry below, replace `"command": "piston", "args": ["mcp"]`
with `"command": "dnx", "args": ["Piston@<version>", "--", "mcp"]`, substituting
an actual version. Retain the worktree working directory. The `mcp` subcommand is
essential: the default Piston command starts/attaches to a daemon rather than
serving stdio MCP. This needs the .NET 10 SDK and ASP.NET Core 10 runtime, just
like an installed tool.

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
`webPort` and `stdio`. By default it opens no web ports. Opt in to the dashboard
with `"dashboard": true` or `piston mcp --dashboard`; it binds an ephemeral
`127.0.0.1` web port without changing the MCP stdio transport.

## 3. Connect the agent

### Quick setup with `piston init`

The tool carries its harness assets: the MCP server entry, a `piston` skill
that teaches the verification contract (section 4), and, for Copilot, the
`piston-dashboard` canvas. `piston init` prints what it would install and where;
it changes nothing until you add `--write`:

```sh
piston init copilot                  # dry run: the MCP entry and every target file
piston init copilot --write          # install for every Copilot session (user scope)
piston init claude --write           # Claude Code: the skill, plus the `claude mcp add` command to run
piston init claude --write --scope repo   # this repository: .mcp.json and .claude/skills/piston
```

| Option | Effect |
|---|---|
| `--write` | Write the files. Without it, `piston init` only prints. |
| `--scope user` (default) | Copilot: `$COPILOT_HOME` (default `~/.copilot`): `mcp-config.json`, `skills/piston/` and `extensions/piston-dashboard/`. Claude Code: `$CLAUDE_CONFIG_DIR/skills/piston/` (default `~/.claude`). |
| `--scope repo` | The current directory: `.mcp.json` (shared by Copilot and Claude Code), plus `.github/skills/piston/` and `.github/extensions/piston-dashboard/` for Copilot, or `.claude/skills/piston/` for Claude Code. Commit them to share the setup. |
| `--dashboard` | Register `piston mcp --dashboard`, so each session serves the dashboard the canvas shows. |
| `--force` | Replace a file or `piston` entry whose content differs from the bundled one. |

`piston init` merges only the `piston` entry into `mcpServers` and keeps every other
server. Files that already match are reported `unchanged`, so rerunning is safe. A
file or entry that differs (your own edits, or an older Piston's assets) is left as
it is and reported. With `--write` the command then exits 1 until you rerun it
with `--force`. A config file that is not plain JSON with an `mcpServers` object
(for example one with comments) is never rewritten. `piston init` reports it as
`invalid` and exits 1; add the entry by hand.

At user scope, `piston init claude` does not edit `~/.claude.json`, which also
holds Claude Code's other state. It prints the registration command instead:

```sh
claude mcp add --scope user --transport stdio piston -- piston mcp
```

Start a new agent session after installing. Copilot loads project MCP servers from
`.mcp.json` only in trusted folders, and Claude Code asks you to approve a
project's `.mcp.json` servers on first use.

### GitHub Copilot CLI (manual setup)

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

For the skill and canvas without `piston init`, copy this repository's
`src/Piston/Integration/Assets/SKILL.md` to `~/.copilot/skills/piston/SKILL.md`, and
`.github/extensions/piston-dashboard/` (without the `*.test.mjs` files) to
`~/.copilot/extensions/piston-dashboard/`.

### Claude Code (manual setup)

Run the `claude mcp add` command above, or add the same server to a repository's
`.mcp.json`:

```json
{
  "mcpServers": {
    "piston": {
      "type": "stdio",
      "command": "piston",
      "args": ["mcp"]
    }
  }
}
```

Copy the skill to `~/.claude/skills/piston/SKILL.md` (or `.claude/skills/piston/`
in the repository). `piston mcp` resolves the solution from its working
directory, so start Claude Code from the worktree root.

### Other MCP clients

Any MCP client that can launch a stdio server works:

- **Command.** `piston` with arguments `mcp` (add `--dashboard` for the web
  dashboard).
- **Working directory.** The worktree root, which is where the solution is resolved
  (section 2). Alternatively, pass the solution path: `piston mcp path/to/App.slnx`.
- **One process per session.** Each agent session needs its own process. Do not
  share one stdio server between worktrees.
- **Instructions.** Give the agent the [custom instructions snippet](#custom-instructions-snippet)
  or the `piston` skill text.

A client that only speaks HTTP can use the [HTTP daemon](#http-daemon-humans-and-the-dashboard)
instead.

### What each `piston mcp` process does

Copilot CLI and Claude Code start stdio MCP servers in the session's working
directory, normally the worktree. Each session therefore gets its own `piston mcp`
process, engine and results. Nothing needs to run before the agent starts. Per
process:

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
Use `get_test_result` to retrieve one test's standard output, standard error,
failure details and attachment metadata without reading the full run. It accepts
a fully-qualified name or method name, and can select the active or a specific
run and disambiguate by project.
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

For the dashboard attached to an existing stdio session, use its `--dashboard`
opt-in rather than starting a competing owner. This repository's native
`piston-dashboard` canvas discovers the active worktree and shows the existing
web UI, including a disconnected state. `piston dashboard --json` inspects the
owner without starting an engine or a run. See the [canvas setup](../README.md#copilot-canvas).

To share one engine through HTTP between a human and an agent, run the HTTP
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

Piston edits an agent's configuration only through `piston init … --write`, and never changes the daemon's requested ports. The
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

After a daemon restart, `get_test_results` may immediately return the previous
session's saved suites. They are marked restored and not re-verified, with
freshness `Unknown`; `verify` forces a new run rather than reusing them.

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

With coverage enabled, `get_changed_line_coverage` gives each changed line a
verdict (see below). It takes no `files`: it always reads the git changes given
by `base` and `baseRef`.

Selection by changed lines is a heuristic, and an affected-test result does not
count as verification, even when the plan's scope is `full`. Finish with `verify`. See
[Coverage and impact selection](../README.md#coverage-and-impact-selection) for
how tests are selected and for the other tools.

### Changed-line coverage verdicts

`get_changed_line_coverage` (schema `piston.changed-line-coverage/v2`) classifies
every changed line of every changed `.cs`, `.fs` or `.vb` file. The same verdicts
are available to protocol clients as `coverage/getChangedLines`, and as a diff
(`coverage/getChangeDiff`) in the dashboard's Coverage › Changes view, which the
`piston-dashboard` canvas opens with `{"view":"changes"}` or its `show_changes`
action.

| Verdict | Meaning |
|---|---|
| `coveredByPassing` | The line ran, and every current test credited with executing it passed. |
| `coveredOnlyByFailing` | The line ran, and every current test credited with executing it failed. The file's `failingTests` names them. |
| `uncovered` | The line is executable and no test that ran executed it. |
| `nonExecutable` | The file was measured as it is now and the line has no sequence point, for example a blank line, a comment or most declarations. |
| `unknown` | The evidence cannot prove anything about the line; `reason` says why. |

| Unknown reason | Meaning | What to do |
|---|---|---|
| `stale` | The line's code changed after it was measured, so the coverage describes old code (lines in unchanged C# members keep their remapped coverage and get verdicts); or, for a `range`, the file differs between the range's last commit and the working tree, so the changed line numbers do not match the measured file; or the file kept changing while the verdicts were read (every changed line is then `stale`). | Run the affected tests (`run_affected_tests`) and ask again. |
| `notMeasured` | No coverage run measured the file, for example it is not compiled into a tested assembly. | Run the tests, or accept that the file is not covered by tests. |
| `noTestEvidence` | The line ran, but the impact map has no current record of which tests executed it. | Run the affected tests. |
| `mixedEvidence` | Passing and failing tests are both credited with the line. | Fix the failing tests, then ask again. |
| `testsNotRun` | A credited test has no result yet (or is still running), or every credited test was skipped. | Run the affected tests. |
| `outdatedEvidence` | Evidence recorded against older content may credit other tests with the line, and their results would change the verdict. | Run the affected tests. |
| `resultsUpdating` | A run was publishing new coverage and test results while the verdicts were read, so the coverage and its tests' results may come from different runs. Every changed line is then `unknown`. | Ask again when the run finishes. |

Each file reports `measured`, `isStale`, `isPartial` (only a selective run measured
it, so tests that did not run may cover more), per-verdict line ranges, `unknown`
groups with their reason, and `partialBranches` (covered lines where some
branches never ran). The top level has `totals` and an `unknownReasons` count for
every reason. Warnings explain caveats, such as a run in progress.

How to read the verdicts:

- Only `coveredByPassing` means passing tests executed the line. Never describe
  an `unknown` line as covered, and never treat a `stale` line's old coverage as
  evidence about the current code. A file with `isStale` can still have verdicts
  for changed lines in members whose code is unchanged since it was measured.
- The impact map records which methods each test (or test class, or whole test
  project run) executed, not individual lines. A test can be credited with a line
  of a method it entered without executing that line. Because of this, a line is
  `coveredByPassing` only when every credited test passed; any mix gives `unknown`.
- A credited test's result is its latest result in its own test project. A test
  with no result there counts as not run, even if another project has a test of
  the same name. Projects are told apart by their solution-relative paths, so
  `One/Tests.csproj` and `Two/Tests.csproj` keep separate results. Results
  recorded without a project path are matched by project file name, and count
  as not run when two projects share that name.
- Known limitation: while a file's content is unchanged, a selective run adds its
  hits to the stored coverage instead of replacing them, and the coverage does not
  record which run hit each line. Verdicts use each credited test's latest result.
  So if a test failed in a run that executed a line and later passed a selective
  rerun that did not execute it, the line can still be `coveredByPassing`. A full,
  unfiltered run replaces every file's coverage and removes such hits.
- Coverage verdicts describe test reach, not correctness. They do not replace
  `verify`.

## Custom instructions snippet

`piston init` installs the `piston` skill, which carries these rules in more
detail. For agents without skills, paste this into the repository's agent
instructions (for example `.github/copilot-instructions.md`):

```markdown
## Testing with Piston

- The `piston` MCP server (`piston mcp`, one per session) tests this worktree.
- To check whether the code builds and tests pass, call the `verify` tool instead of
  running `dotnet build` / `dotnet test`.
- Only `Verdict: Passed` with `Counts as full verification: yes` counts as passing.
- On `TestsFailed` / `BuildFailed`, fix the listed tests or errors and call
  `verify` again.
- On `Stale` or `TimedOut`, call `verify` again; do not report success.
- Do not use `get_test_results` or filtered runs as proof that everything passes,
  and do not set test filters while verifying.
- The `Solution:` line in the reply must be this worktree's solution.
- If the `piston` MCP server is unavailable, a tool returns an error (for example
  "Piston owned by another process" or "could not start"), or `verify` returns
  `Faulted`/`Cancelled`, run `dotnet test` explicitly and say that you fell back
  to it.
```
