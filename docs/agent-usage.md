# Using Piston as a coding agent's test backend

This is the minimal dogfood flow: one developer, one Linux worktree, one daemon,
one coding agent connected over HTTP MCP. It is a usability step, not evidence for
gate G1 or any performance claim.

## 1. Install the local tool

```sh
dotnet pack src/Piston/Piston.csproj -c Release -o packages
dotnet tool install Piston --version 0.1.0 --tool-path ./tools --source ./packages --no-cache
```

To pick up a rebuilt package later, uninstall first (`dotnet tool uninstall Piston
--tool-path ./tools`) or pack with a new version (`-p:Version=0.1.0-dev.2`) and
use `dotnet tool update`. With `--no-cache`, NuGet does not reuse a stale copy of
the same version. The examples below assume `piston` resolves to that install.

## 2. Configure the worktree

In the worktree root, create `.piston.json`:

```json
{
  "solution": "MyApp.slnx",
  "mcpPort": 5200
}
```

`solution` is resolved relative to the file. Pick an `mcpPort` that is unique per
running daemon. Start the daemon in the foreground, in its own terminal:

```sh
piston daemon            # add --web-port <port> if 5199 is taken
```

Startup problems fail fast with `error: ...` and exit code 1. Examples:
- another daemon already owns this worktree;
- the MCP or web port is in use: `error: Could not start the MCP server on ...`.

Piston binds its pipe, web and MCP endpoints after taking worktree ownership but
before it starts watching or runs the startup build, so a port conflict starts no
build or test run. Piston never edits the agent's configuration, and it never
picks another port. Fix the port and start again.

Check and stop it from the same directory:

```sh
piston status   # PID, solution, phase, web and MCP URLs, latest/active run
piston stop     # asks the daemon to shut down and waits for the process to exit
```

`piston stop` exits 0 only after the daemon process has exited, so its ports are
free. If no daemon is running it prints `No daemon running` and also exits 0;
it exits non-zero if the daemon does not exit in time.

## 3. Connect the agent

The MCP endpoint is the root URL (`http://localhost:<mcpPort>/`, not `/mcp`). For
Copilot CLI, add a server entry to the repository's `.mcp.json` (or your user MCP
config). Merge it with the existing servers rather than replacing them:

```json
{
  "mcpServers": {
    "piston": {
      "type": "http",
      "url": "http://localhost:5200/",
      "tools": ["*"]
    }
  }
}
```

Start the daemon before the agent session, so that the agent can connect.

## 4. The verification contract

Call `verify` (optional `timeoutSeconds`, default 600) whenever you need to know
whether the current code builds and every test passes. It:

1. waits briefly for pending file-change events to settle and fingerprints the
   source tree, then reevaluates the solution's project graph to check that user
   projects, imports and declared file inputs are covered by that fingerprint;
2. reuses the latest run if it is a current, full-scope, unfiltered result for
   exactly that fingerprint. No build happens and the reply says `Reused: yes`;
3. otherwise awaits an already-running full run, or requests a forced full
   build + test run;
4. if the source changes while that run is in progress (or the run is superseded),
   retries against the new source, at most twice, instead of reporting an old
   result. If the third attempt still has no current result, the verdict is
   `Stale`. The default overall timeout is 10 minutes (`timeoutSeconds`).

The present fingerprint is recaptured even when reusing a result. External
projects (including transitive `ProjectReference`s), linked source files, and
custom imports outside the fingerprint produce `NotFullScope`, not a pass.
The nearest ancestor build/configuration files remain covered; SDK/workload
installations, restored packages and their generated restore imports remain
toolchain/cache exclusions. If coverage evaluation fails, `verify` reports
`Faulted`. Use `dotnet test` explicitly for unsupported inputs; Piston does not
silently fall back. Edits after a verification reply require a new call.

A file-watcher error (including buffer overflow) makes source monitoring
unreliable. `verify` reports `Faulted` until the daemon is restarted; matching
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
| `Cancelled` | The daemon is shutting down. | Restart the daemon or fall back to `dotnet test` explicitly. |
| `TimedOut` | No result within `timeoutSeconds`. The run may still be queued or running. | Call `verify` again (it awaits the active run) or raise the timeout. |
| `NotFullScope` | A filter is active, or evaluated user build inputs are not fingerprinted. | Clear the filter, or use `dotnet test` explicitly for unsupported inputs. |
| `Faulted` | The daemon or pipeline failed. | Fall back to `dotnet test` explicitly and report it. |

An `Admission:` line means the run waited for the machine execution budget (queue
position and wait time). That is expected under load, not a failure.

Do not treat these as verification:
- `get_test_results`: accumulated state that may combine several runs;
- filtered or selective runs;
- a `run_tests` reply read after further edits.

`get_test_run("<runId>")` gives the full JSON record behind any reply.

If the daemon is not reachable, say so and run `dotnet test` explicitly. Never
report a Piston pass that you did not receive.

## Custom instructions snippet

Paste into the repository's agent instructions (for example
`.github/copilot-instructions.md`):

```markdown
## Testing with Piston

- A Piston daemon serves this worktree over the `piston` MCP server.
- To check whether the code builds and tests pass, call the `verify` tool instead of
  running `dotnet build` / `dotnet test`.
- Only `Verdict: Passed` with `Counts as full verification: yes` counts as passing.
- On `TestsFailed` / `BuildFailed`, fix the listed tests or errors and call
  `verify` again.
- On `Stale`, `Cancelled` or `TimedOut`, call `verify` again; do not report success.
- Do not use `get_test_results` or filtered runs as proof that everything passes,
  and do not set test filters while verifying.
- If the `piston` MCP server is unavailable or `verify` returns `Faulted`, run
  `dotnet test` explicitly and say that you fell back to it.
```
