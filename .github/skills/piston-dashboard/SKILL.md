---
name: piston-dashboard
description: Run Piston against this repo (dogfooding) and open its native Copilot dashboard canvas. Use when asked to start, restart, stop or redeploy the daemon, open or refresh the dashboard, or verify a dashboard or engine change live.
---

# Running the Piston daemon and dashboard

Piston dogfoods itself: the daemon watches `Piston.slnx`, builds and runs its tests, and serves the dashboard at `http://localhost:5199` (MCP at `http://localhost:5200`).

## Inspect before starting

Opening a dashboard is not permission to start an engine or tests. First run
`piston dashboard --json` from the active session's worktree. This command only
inspects its live owner's endpoint; it does not start Piston or open a browser.
Never assume that port 5199 belongs to this worktree.

For an already-running stdio MCP session, dashboard hosting is opt-in:
`piston mcp --dashboard`, or `"dashboard": true` in `.piston.json` before the
session starts. It binds an ephemeral `127.0.0.1` port. Do not start a competing
daemon when MCP already owns the worktree. Changing the option requires restarting
that MCP session, which performs the engine's normal startup run.

## Start

1. Build Release: `dotnet build Piston.slnx -c Release -v q -nologo`.
2. Start the daemon **from the Release output**, as a detached async command so it outlives the turn:

   ```sh
   dotnet src/Piston/bin/Release/net10.0/Piston.dll daemon Piston.slnx
   ```

   The daemon builds the watched solution in Debug, so its own builds never overwrite the Release binaries it runs from. Do not use `dotnet run` or a Debug build for the daemon.
3. Confirm it is listening: `ss -ltnp | grep ':5199 '` (or `dotnet src/Piston/bin/Release/net10.0/Piston.dll status`).

Use `daemon`, not bare `piston`/`Piston.dll` with no command: that form opens the system browser via `xdg-open`/`open`.

## Open in the canvas

The project extension lives at `.github/extensions/piston-dashboard/`. It requires
a Copilot runtime with extension canvas support and a canvas-capable `piston` on
PATH (after source changes, use the repository's normal packed-tool installation
flow if the installed tool is older).

1. Inspect capabilities for canvas ID `piston-dashboard`.
2. Open it with a stable instance ID, such as `piston-panel`, and empty input `{}`.
3. Use its `refresh` action to rediscover the current worktree without running
   tests. Reopening or resuming also only inspects state.

The connection shell survives engine loss, displays instructions when hosting is
disabled, and reconnects to a replacement owner of the same worktree. It follows
the app's theme. Closing the canvas does not stop Piston. The `run` and
`run_failed` actions explicitly request execution; do not invoke them merely to
open or refresh the dashboard.

If extension registration/reload tools are unavailable or the runtime has no
native canvas support, say so and open the URL returned by discovery in the
`browser` canvas instead. Reuse a stable instance ID. After redeploying the
dashboard, reopen the panel; for browser fallback, add/increment `?v=<N>` to
avoid stale assets.

## Redeploy after a change

1. Stop the daemon: `dotnet src/Piston/bin/Release/net10.0/Piston.dll stop` (exits 0 once the process has gone, freeing the ports). If that fails, find the PID with `ss -ltnp | grep ':5199 '` and `kill <pid>`.
2. If dashboard Tailwind classes changed: `cd src/Piston.Web && npm run build:css`.
3. Rebuild Release, start the daemon again (detached), and reopen the native canvas.
   Reload the extension after changing its provider/assets. For browser fallback,
   reopen the discovered URL with a bumped `v`.

## Diagnose

- `.piston/diagnostics.log` (solution directory) records the runner choice, commands, build skips and fallbacks. It is truncated when the daemon starts.
- A project node's details pane in the dashboard shows the run log of that project's latest run.
- Only one daemon or `piston mcp` process may own a worktree. If startup fails with an ownership error, run `status --all` and stop the named owner.

## Rules

- Never kill processes by name (`pkill`, `killall`); use `stop` or a specific PID.
- Leave the daemon running when handing back to the user if they are using the dashboard. Otherwise stop it before finishing.
