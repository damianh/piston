---
name: piston-dashboard
description: Run the Piston daemon against this repo (dogfooding) and show its web dashboard in a Copilot browser canvas. Use when asked to start, restart, stop or redeploy the daemon, open or refresh the dashboard, or verify a dashboard or engine change live.
---

# Running the Piston daemon and dashboard

Piston dogfoods itself: the daemon watches `Piston.slnx`, builds and runs its tests, and serves the dashboard at `http://localhost:5199` (MCP at `http://localhost:5200`).

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

Open the `browser` canvas at `http://localhost:5199/?v=<N>`. Reuse one stable `instanceId` (e.g. `piston-dashboard`) so later opens refresh the same panel.

The canvas caches the Blazor WebAssembly app. After redeploying the dashboard, increment `v` (e.g. `?v=61` → `?v=62`) to force a fresh load.

## Redeploy after a change

1. Stop the daemon: `dotnet src/Piston/bin/Release/net10.0/Piston.dll stop` (exits 0 once the process has gone, freeing the ports). If that fails, find the PID with `ss -ltnp | grep ':5199 '` and `kill <pid>`.
2. If dashboard Tailwind classes changed: `cd src/Piston.Web && npm run build:css`.
3. Rebuild Release, start the daemon again (detached), and reopen the canvas with a bumped `v`.

## Diagnose

- `.piston/diagnostics.log` (solution directory) records the runner choice, commands, build skips and fallbacks. It is truncated when the daemon starts.
- A project node's details pane in the dashboard shows the run log of that project's latest run.
- Only one daemon or `piston mcp` process may own a worktree. If startup fails with an ownership error, run `status --all` and stop the named owner.

## Rules

- Never kill processes by name (`pkill`, `killall`); use `stop` or a specific PID.
- Leave the daemon running when handing back to the user if they are using the dashboard. Otherwise stop it before finishing.
