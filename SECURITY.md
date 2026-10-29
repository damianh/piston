# Security

Piston is a local development tool. The daemon runs builds and tests for the solution it
watches, which means it runs that solution's code, as the user who started it.

## What the daemon exposes

| Endpoint | Default | Who can reach it |
|---|---|---|
| Named pipe (JSON-RPC) | per worktree | processes of the same OS user |
| Web dashboard and `/ws` WebSocket | `http://localhost:5199` | anything on this machine that can connect to loopback |
| MCP over streamable HTTP | `http://localhost:5200` | anything on this machine that can connect to loopback |

Both HTTP endpoints listen on `localhost` only and have no authentication. Through them a
client can read test results, output, retained test artifacts and coverage, start runs, clear results, change the
filter and user settings, and stop the daemon.

## Threat model

**In scope: web pages.** A page open in any browser on the machine can send requests to
`localhost`. Piston refuses those requests:

- **DNS rebinding.** A hostile name that resolves to `127.0.0.1` would make a page
  same-origin with the dashboard. Piston accepts only local `Host` names: `localhost`,
  `*.localhost` and loopback addresses. Other host names get `400`. Add names, for example
  for a reverse proxy, with `PISTON_ALLOWED_HOSTS`, a list separated by `;` or `,`.
- **Cross-site WebSocket and POST.** WebSockets are not covered by CORS, so any page can
  open one. A request whose `Origin` is not the requested scheme, host and port gets `403`.
  This covers `null` and `file://` origins. A WebSocket or a non-GET request without
  `Origin` that the browser marks `Sec-Fetch-Site: cross-site` is also refused. Clients
  that send no `Origin` header, such as MCP clients and scripts, are allowed. Piston does
  not send CORS headers.
- The dashboard responses carry `X-Content-Type-Options: nosniff`,
  `Referrer-Policy: no-referrer`, `Cross-Origin-Opener-Policy: same-origin` and
  `Cross-Origin-Resource-Policy: same-origin`, and a `Content-Security-Policy` that
  allows scripts only from the daemon (plus `'wasm-unsafe-eval'` for WebAssembly and the
  build's inline import map by hash) and no plugins. Inline styles are allowed. The
  policy sets no `frame-ancestors`, so tools that embed the dashboard keep working.

**Out of scope: local processes.** Any process of any user on the machine that can
connect to loopback can use the HTTP endpoints. A local process can already run code as
someone, so Piston does not try to separate local users. Do not run the daemon on shared
multi-user hosts, and do not expose its ports through a tunnel or port forward.

**Out of scope: the watched solution.** Builds and tests run the solution's own code
(MSBuild tasks, test code, analyzers) with the daemon user's rights. Only watch code you
would build and test yourself.

## Child processes

Builds and test hosts do not inherit the daemon's listen address, `dotnet watch`/hot
reload, launch profile, IDE debug session or Aspire run variables. See
[Child process environment](README.md#child-process-environment).

## Reporting a vulnerability

Report vulnerabilities privately through
[GitHub security advisories](https://github.com/damianh/piston/security/advisories/new)
rather than a public issue.
