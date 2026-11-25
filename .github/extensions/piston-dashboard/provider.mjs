// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

import { execFile, spawn } from "node:child_process";
import { promisify } from "node:util";
import { createServer, request } from "node:http";
import { mkdtemp, open, readFile, realpath, stat } from "node:fs/promises";
import { existsSync, realpathSync, statSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

const execute = promisify(execFile);
const assets = new Map(await Promise.all(["shell.html", "shell.js", "shell.css"].map(async name =>
    [name, await readFile(new URL(name, import.meta.url))])));

export function normalizeWorkingDirectory(cwd, platform = process.platform) {
    if (platform !== "win32") return cwd;
    let ordinary;
    if (/^\\\\\?\\UNC\\/i.test(cwd)) ordinary = `\\\\${cwd.slice(8)}`;
    else if (/^\\\\\?\\[a-z]:\\/i.test(cwd)) ordinary = cwd.slice(4);
    else return cwd;
    const root = path.win32.parse(ordinary).root;
    for (const segment of ordinary.slice(root.length).split("\\").filter(Boolean)) {
        const name = segment.split(".")[0];
        if (/[. ]$/.test(segment) || /^(con|prn|aux|nul|conin\$|conout\$|com[1-9]|lpt[1-9])$/i.test(name)) return cwd;
    }
    return ordinary;
}

export function loopbackEndpoint(value) {
    const url = new URL(value);
    if (url.protocol !== "http:" || !["localhost", "127.0.0.1", "[::1]"].includes(url.hostname)
        || url.username || url.password || url.pathname !== "/" || url.search || url.hash) {
        throw new Error("Piston advertised an invalid loopback dashboard endpoint.");
    }
    return url;
}

export function resolveExecutable(command, cwd, {
    platform = process.platform,
    pathValue = process.env.PATH ?? "",
    pathExt = process.env.PATHEXT ?? ".COM;.EXE;.BAT;.CMD",
} = {}) {
    const pathApi = platform === "win32" ? path.win32 : path;
    const directory = realpathSync.native(cwd);
    let root = directory;
    for (let current = directory; ;) {
        if (existsSync(pathApi.join(current, ".git"))) {
            root = current;
            break;
        }
        const parent = pathApi.dirname(current);
        if (parent === current) break;
        current = parent;
    }
    const insideRoot = candidate => {
        const relative = pathApi.relative(root, candidate);
        return relative === "" || (!pathApi.isAbsolute(relative) && relative !== ".."
            && !relative.startsWith(`..${pathApi.sep}`));
    };
    const extensions = platform === "win32"
        ? (path.extname(command) ? [""] : pathExt.split(";").filter(extension => [".COM", ".EXE", ".BAT", ".CMD"].includes(extension.toUpperCase())))
        : [""];

    for (const entry of pathValue.split(pathApi.delimiter)) {
        if (!entry || !pathApi.isAbsolute(entry)) continue;
        let directory;
        try {
            directory = realpathSync.native(entry);
        } catch {
            continue;
        }
        if (insideRoot(directory)) continue;

        for (const extension of extensions) {
            const candidate = pathApi.join(directory, pathApi.extname(command) ? command : command + extension);
            try {
                if (!existsSync(candidate) || !statSync(candidate).isFile()) continue;
                const resolved = realpathSync.native(candidate);
                if (!insideRoot(resolved)) return resolved;
            } catch {
                continue;
            }
        }
    }

    throw new Error(`Could not resolve '${command}' from an absolute PATH entry outside worktree '${root}'.`);
}

/**
 * How to start a resolved executable. Node cannot start a Windows batch file (the Piston tool's
 * `piston.cmd` entry point) without cmd.exe, so batch files run through cmd.exe with every argument quoted.
 * Characters that cmd.exe expands or cannot quote are rejected rather than escaped.
 */
export function commandLine(file, args, platform = process.platform, comSpec = process.env.ComSpec) {
    if (platform !== "win32" || ![".bat", ".cmd"].includes(path.win32.extname(file).toLowerCase())) {
        return { file, args, options: {}, wrapped: false };
    }
    const quote = value => {
        if (/["%\r\n\0]/.test(value)) {
            throw new Error(`Cannot pass '${value}' to '${file}': it contains a character cmd.exe cannot quote safely.`);
        }
        return `"${value}"`;
    };
    const command = [file, ...args].map(quote).join(" ");
    return {
        file: comSpec || "cmd.exe",
        args: ["/d", "/s", "/v:off", "/c", `"${command}"`],
        options: { windowsVerbatimArguments: true },
        wrapped: true,
    };
}

const changeBases = ["working", "branch", "range"];
const baseRefPattern = /^[A-Za-z0-9._/@{}~^][A-Za-z0-9._/@{}~^-]*$/;

/**
 * The dashboard query that opens the Coverage tab's Changes view, e.g.
 * `tab=coverage&mode=changes&base=branch&ref=main`. A ref is a branch, commit or range; it may not start with
 * `-`, so it can never become a git option.
 */
export function changesQuery({ base, baseRef } = {}) {
    if (base !== undefined && !changeBases.includes(base)) {
        throw new Error(`base must be one of ${changeBases.join(", ")}.`);
    }
    if (baseRef !== undefined && (typeof baseRef !== "string" || baseRef.length > 200 || !baseRefPattern.test(baseRef))) {
        throw new Error("baseRef must be a branch, commit or range of at most 200 characters, not starting with '-'.");
    }
    const chosen = base ?? (baseRef ? "branch" : "working");
    if (chosen === "range" && !baseRef) throw new Error("A range needs baseRef, e.g. HEAD~3..HEAD.");
    const query = new URLSearchParams({ tab: "coverage", mode: "changes", base: chosen });
    if (chosen !== "working" && baseRef) query.set("ref", baseRef);
    return query.toString();
}

/** The dashboard query an open request asks for, or undefined to leave the panel where it is. */
export function openQuery(input) {
    if (input === undefined || input === null) return undefined;
    if (typeof input !== "object" || Array.isArray(input)) throw new Error("The canvas input must be an object.");
    const { view, base, baseRef } = input;
    if (view !== undefined && !["dashboard", "changes"].includes(view)) throw new Error("view must be dashboard or changes.");
    if (view === "dashboard") {
        if (base !== undefined || baseRef !== undefined) throw new Error("base and baseRef apply only to the changes view.");
        return "";
    }
    if (view === undefined && base === undefined && baseRef === undefined) return undefined;
    return changesQuery({ base, baseRef });
}

export async function discover(cwd, executeCommand = execute, resolve = resolveExecutable, platform = process.platform) {
    try {
        let stdout;
        let failed = false;
        try {
            const command = commandLine(resolve("piston", cwd), ["dashboard", "--json"], platform);
            ({ stdout } = await executeCommand(command.file, command.args, {
                cwd, timeout: 5000, maxBuffer: 64 * 1024, windowsHide: true, ...command.options,
            }));
        } catch (error) {
            if (error.code !== 1 || typeof error.stdout !== "string") throw error;
            stdout = error.stdout;
            failed = true;
        }
        const state = JSON.parse(stdout);
        if (!state || !["connected", "disconnected", "disabled", "error"].includes(state.state)
            || typeof state.message !== "string" || (failed && state.state !== "error")) {
            throw new Error("Unexpected discovery response; upgrade Piston to a version with canvas support.");
        }
        return state;
    } catch (error) {
        throw new Error(`Could not inspect Piston: ${error.message}. Ensure a canvas-capable 'piston' is on PATH.`, { cause: error });
    }
}

export async function probe(state) {
    const target = loopbackEndpoint(state.url);
    const response = await fetch(new URL("api/dashboard", target), {
        signal: AbortSignal.timeout(3000), redirect: "error",
    });
    if (!response.ok) throw new Error(`Dashboard health check returned HTTP ${response.status}.`);
    const owner = await response.json();
    if (owner.processId !== state.processId || owner.worktreePath !== state.worktree
        || owner.acquiredAt !== state.acquiredAt) {
        throw new Error("The dashboard no longer belongs to the discovered worktree owner. Retry discovery.");
    }
    return target;
}

export async function discoverSolutions(worktree, cwd, executeCommand = execute, resolve = resolveExecutable) {
    const root = await realpath(worktree);
    const base = await realpath(cwd);
    const { stdout } = await executeCommand(resolve("git", root), [
        "-c", "core.fsmonitor=false", "ls-files", "--cached", "--others", "--exclude-standard", "--full-name", "-z", "--",
        ":(icase,glob)**/*.sln", ":(icase,glob)**/*.slnx", ":(icase,glob)**/*.slnf",
    ], { cwd: root, timeout: 10000, maxBuffer: 8 * 1024 * 1024, windowsHide: true });
    const files = await Promise.all(stdout.split("\0").filter(Boolean).map(async name => {
        const candidate = path.resolve(root, ...name.split("/"));
        try {
            const resolved = await realpath(candidate);
            const relative = path.relative(root, resolved);
            if (!relative || relative === ".." || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)
                || !(await stat(resolved)).isFile()) return null;
            return path.relative(base, candidate);
        } catch (error) {
            if (error.code === "ENOENT") return null;
            throw error;
        }
    }));
    return [...new Set(files.filter(Boolean))].sort((a, b) => a.localeCompare(b));
}

async function worktreeRoot(directory) {
    let current = directory;
    for (;;) {
        try {
            await stat(path.join(current, ".git"));
            return current;
        } catch (error) {
            if (error.code !== "ENOENT") throw error;
        }
        const parent = path.dirname(current);
        if (parent === current) return directory;
        current = parent;
    }
}

export async function startDaemon(cwd, solution, worktree, inspect = discover, check = probe, launch = spawn, logRoot = tmpdir(), platform = process.platform, resolve = resolveExecutable) {
    if (typeof solution !== "string" || !solution.trim() || solution.length > 4096 || solution.includes("\0")) {
        throw new Error("Enter a solution path.");
    }
    const root = await realpath(worktree);
    const selected = await realpath(path.resolve(cwd, solution));
    const relative = path.relative(root, selected);
    if (!relative || relative.startsWith(`..${path.sep}`) || relative === ".." || path.isAbsolute(relative)
        || ![".sln", ".slnx", ".slnf"].includes(path.extname(selected).toLowerCase())
        || !(await stat(selected)).isFile()) {
        throw new Error("Choose a .sln, .slnx, or .slnf file inside this worktree.");
    }
    const [canvasWorktree, solutionWorktree] = await Promise.all([
        worktreeRoot(root),
        worktreeRoot(path.dirname(selected)),
    ]);
    if (path.relative(canvasWorktree, solutionWorktree) || path.relative(solutionWorktree, canvasWorktree)) {
        throw new Error("Choose a solution whose canonical worktree matches this canvas.");
    }
    const logDirectory = await mkdtemp(path.join(logRoot, "piston-canvas-"));
    const logPath = path.join(logDirectory, "daemon.log");
    const log = await open(logPath, "w");
    let child;
    let failure;
    let command;
    const launchedAt = Date.now();
    try {
        command = commandLine(resolve("piston", cwd), ["daemon", selected, "--no-stdio", "--config-directory", cwd], platform);
        // On Windows, detached means DETACHED_PROCESS: the daemon would have no console, so every console
        // process it starts would open a visible window. Give it a hidden console instead; it still outlives the canvas.
        child = launch(command.file, command.args, {
            cwd, detached: platform !== "win32", stdio: ["ignore", log.fd, log.fd], windowsHide: true, ...command.options,
        });
        child.once("error", error => { failure = error.message; });
        child.once("exit", code => { failure = `Piston exited during startup (code ${code}).`; });
    } finally {
        await log.close();
    }
    child.unref();
    const deadline = Date.now() + 30_000;
    let probeError;
    while (Date.now() < deadline) {
        if (failure) {
            const details = (await readFile(logPath, "utf8")).trim().slice(-4000);
            throw new Error(`${failure}${details ? ` ${details}` : ""} Log: ${logPath}`);
        }
        const state = await inspect(cwd);
        if (state.state === "connected") {
            // A batch launcher runs the daemon as a descendant of cmd.exe, so its PID is unknown. The launcher
            // stays alive while the daemon runs, so accept an owner that took the worktree after this launch.
            const ours = command.wrapped
                ? Date.parse(state.acquiredAt) >= launchedAt - 2000
                : state.processId === child.pid;
            if (!ours) {
                throw new Error("Another Piston owner started first. Retry the connection.");
            }
            try {
                await check(state);
                return state;
            } catch (error) {
                probeError = error.message;
            }
        }
        if (state.state === "error") throw new Error(state.message);
        await new Promise(resolve => setTimeout(resolve, 200));
    }
    throw new Error(`Piston did not become ready within 30 seconds; it may still be starting.${probeError ? ` ${probeError}` : ""} Log: ${logPath}`);
}

export function rpc(target, method, { timeout = 600_000, signal } = {}) {
    return new Promise((resolve, reject) => {
        if (signal?.aborted) { reject(new Error("The canvas was closed.")); return; }
        let settled = false;
        const url = new URL("/ws", target);
        url.protocol = "ws:";
        const socket = new WebSocket(url);
        const timer = setTimeout(() => finish(new Error("Piston request timed out; the run may still be active.")), timeout);
        function finish(error, result) {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            signal?.removeEventListener("abort", aborted);
            socket.close();
            if (error) reject(error); else resolve(result);
        }
        function aborted() { finish(new Error("The canvas was closed; the run may still be active.")); }
        signal?.addEventListener("abort", aborted, { once: true });
        socket.addEventListener("open", () => socket.send(JSON.stringify({ jsonrpc: "2.0", id: "canvas-action", method })));
        socket.addEventListener("message", event => {
            let message;
            try { message = JSON.parse(event.data); }
            catch { finish(new Error("Piston returned invalid JSON-RPC.")); return; }
            if (message.id !== "canvas-action") return;
            finish(message.error ? new Error(message.error.message) : null, { method, result: message.result });
        });
        socket.addEventListener("error", () => finish(new Error("Could not connect to the Piston engine.")));
        socket.addEventListener("close", () => finish(new Error("The engine disconnected before replying; inspect state before retrying.")));
    });
}

export function createDashboardCanvas({ inspect = discover, check = probe, invoke = rpc, start = startDaemon } = {}) {
    const panels = new Map();
    const pending = new Map();
    const starting = new Map();

    async function create(cwd) {
        let state = { state: "disconnected", message: "Inspecting the current worktree..." };
        let target;
        let refreshing;
        let checkedAt = 0;
        let closed = false;
        let navigation = { seq: 0, query: "" };
        const actionLifetime = new AbortController();
        const sockets = new Set();
        async function refresh(force = false) {
            if (closed) throw new Error("The canvas is closed.");
            if (refreshing) return refreshing;
            if (!force && Date.now() - checkedAt < 2000) return state;
            refreshing = (async () => {
                try {
                    const next = await inspect(cwd);
                    const nextTarget = next.state === "connected" ? await check(next) : undefined;
                    if (closed) return state;
                    const identity = nextTarget ? `${next.worktree}:${next.processId}:${next.acquiredAt}` : null;
                    if (identity !== state.identity) {
                        for (const socket of sockets) socket.destroy();
                        sockets.clear();
                    }
                    target = nextTarget;
                    state = {
                        ...next,
                        message: next.state === "disabled"
                            ? "The current owner has not published a dashboard. If this persists, upgrade Piston and restart the owner."
                            : next.message,
                        identity,
                    };
                } catch (error) {
                    target = undefined;
                    for (const socket of sockets) socket.destroy();
                    sockets.clear();
                    state = { state: "error", message: error.message, identity: null };
                } finally {
                    checkedAt = Date.now();
                }
                return state;
            })();
            try { return await refreshing; }
            finally { refreshing = undefined; }
        }

        const server = createServer((req, res) => {
            void handle(req, res).catch(error => {
                if (!res.headersSent) { res.writeHead(502, { "Content-Type": "text/plain" }); res.end(error.message); }
                else res.destroy(error);
            });
        });
        await new Promise((resolve, reject) => {
            server.once("error", reject);
            server.listen(0, "127.0.0.1", resolve);
        });
        const origin = `http://127.0.0.1:${server.address().port}`;
        function allowed(req) {
            return req.headers.host === new URL(origin).host
                && (!req.headers.origin || req.headers.origin === origin)
                && (req.headers.upgrade?.toLowerCase() !== "websocket" || req.headers["sec-fetch-site"] !== "cross-site");
        }
        function track(socket) {
            sockets.add(socket);
            socket.once("close", () => sockets.delete(socket));
        }
        async function handle(req, res) {
            const path = req.url.split("?")[0];
            const startRequest = path === "/__piston/start" && req.method === "POST";
            if (!allowed(req) || (!["GET", "HEAD"].includes(req.method) && !startRequest)
                || (startRequest && (req.headers.origin !== origin || req.headers["sec-fetch-site"] === "cross-site"
                    || req.headers["content-type"] !== "application/json"))) {
                res.writeHead(403); res.end("Cross-origin or unsupported request refused."); return;
            }
            res.setHeader("X-Content-Type-Options", "nosniff");
            res.setHeader("Referrer-Policy", "no-referrer");
            res.setHeader("Cache-Control", "no-store");
            if (path === "/__piston/solutions") {
                try {
                    const current = await refresh();
                    if (!current.worktree) throw new Error("The current worktree is unavailable.");
                    res.setHeader("Content-Type", "application/json");
                    res.end(JSON.stringify({ solutions: await discoverSolutions(current.worktree, cwd) }));
                } catch (error) {
                    res.writeHead(500, { "Content-Type": "application/json" });
                    res.end(JSON.stringify({ error: `Could not discover solutions: ${error.message}` }));
                }
                return;
            }
            if (startRequest) {
                try {
                    let body = "";
                    for await (const chunk of req) {
                        body += chunk;
                        if (body.length > 8192) throw new Error("Request is too large.");
                    }
                    const input = JSON.parse(body);
                    if (!input || typeof input.solution !== "string") throw new Error("Enter a solution path.");
                    const current = await refresh(true);
                    if (current.state !== "disconnected") throw new Error(current.message);
                    if (starting.has(cwd)) throw new Error("Piston is already starting for this worktree.");
                    const operation = start(cwd, input.solution, current.worktree, inspect, check);
                    starting.set(cwd, operation);
                    try { await operation; }
                    finally { starting.delete(cwd); }
                    res.setHeader("Content-Type", "application/json");
                    res.end(JSON.stringify(await refresh(true)));
                } catch (error) {
                    res.writeHead(400, { "Content-Type": "application/json" });
                    res.end(JSON.stringify({ error: error.message }));
                }
                return;
            }
            if (path === "/__piston/state") {
                res.setHeader("Content-Type", "application/json");
                res.end(JSON.stringify({ ...await refresh(), navigation })); return;
            }
            const asset = path === "/__piston/" ? "shell.html" : path.slice("/__piston/".length);
            if (path.startsWith("/__piston/") && assets.has(asset)) {
                res.setHeader("Content-Security-Policy", "default-src 'self'; style-src 'self' 'unsafe-inline'; frame-src 'self'; object-src 'none'; base-uri 'none'");
                res.setHeader("Content-Type", asset.endsWith(".js") ? "text/javascript" : asset.endsWith(".css") ? "text/css" : "text/html");
                res.end(assets.get(asset)); return;
            }
            await refresh();
            if (!target) { res.writeHead(503); res.end(state.message); return; }
            const upstreamUrl = new URL(req.url, target);
            if (!req.url.startsWith("/") || upstreamUrl.origin !== target.origin) {
                res.writeHead(403); res.end("Proxy target changes are refused."); return;
            }
            const upstream = request(upstreamUrl, {
                method: req.method,
                headers: proxyHeaders(req.headers, target),
                timeout: 15000,
            }, response => {
                res.writeHead(response.statusCode, { ...response.headers, ...res.getHeaders() });
                response.on("error", error => res.destroy(error));
                response.pipe(res);
            });
            upstream.on("timeout", () => upstream.destroy(new Error("Dashboard request timed out.")));
            upstream.on("error", error => {
                if (!res.headersSent) { res.writeHead(502); res.end(`Dashboard unavailable: ${error.message}`); }
                else res.destroy(error);
            });
            req.on("aborted", () => upstream.destroy());
            res.on("close", () => upstream.destroy());
            upstream.end();
        }

        server.on("upgrade", (req, client, head) => {
            client.on("error", () => client.destroy());
            if (!allowed(req) || req.url !== "/ws") { client.destroy(); return; }
            void refresh().then(() => {
                if (!target || closed) { client.destroy(); return; }
                track(client);
                const upstream = request(new URL("/ws", target), {
                    headers: proxyHeaders(req.headers, target), timeout: 5000,
                });
                upstream.on("upgrade", (response, remote, remoteHead) => {
                    if (closed || client.destroyed) { remote.destroy(); return; }
                    track(remote);
                    client.write(`HTTP/1.1 101 Switching Protocols\r\n${Object.entries(response.headers)
                        .map(([key, value]) => `${key}: ${value}`).join("\r\n")}\r\n\r\n`);
                    if (head.length) remote.write(head);
                    if (remoteHead.length) client.write(remoteHead);
                    client.pipe(remote).pipe(client);
                    remote.on("error", () => client.destroy());
                    client.on("error", () => remote.destroy());
                    client.on("close", () => remote.destroy());
                    remote.on("close", () => client.destroy());
                });
                upstream.on("response", () => { upstream.destroy(); client.destroy(); });
                upstream.on("timeout", () => upstream.destroy());
                upstream.on("error", () => client.destroy());
                client.on("close", () => upstream.destroy());
                upstream.end();
            }).catch(() => client.destroy());
        });
        await refresh();
        return {
            cwd, url: `${origin}/__piston/`,
            refresh,
            navigate(query) {
                if (query !== undefined) navigation = { seq: navigation.seq + 1, query };
                return navigation;
            },
            async action(name, input) {
                if (name === "show_changes") {
                    const query = changesQuery(input ?? {});
                    const current = await refresh(true);
                    this.navigate(query);
                    return { state: current.state, message: current.message, shown: `?${query}` };
                }
                const current = await refresh(true);
                if (name === "refresh") return current;
                if (!target) throw new Error(current.message);
                if (!["run", "run_failed"].includes(name)) throw new Error("Unknown canvas action.");
                return invoke(target, name === "run" ? "engine/forceRun" : "engine/runFailed", { signal: actionLifetime.signal });
            },
            async close() {
                closed = true;
                actionLifetime.abort();
                for (const socket of sockets) socket.destroy();
                server.closeAllConnections();
                await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
            },
        };
    }

    return {
        async open(instanceId, cwd, input) {
            const query = openQuery(input);
            let entry = panels.get(instanceId);
            if (entry && entry.cwd !== cwd) { await this.close(instanceId); entry = undefined; }
            if (!entry) {
                let opening = pending.get(instanceId);
                if (!opening) {
                    opening = create(cwd);
                    pending.set(instanceId, opening);
                }
                try {
                    entry = await opening;
                    if (entry.cwd !== cwd) throw new Error("The active worktree changed while opening; reopen the canvas.");
                    panels.set(instanceId, entry);
                }
                finally { pending.delete(instanceId); }
            }
            entry.navigate(query);
            await entry.refresh(true);
            return { url: entry.url, title: "Piston", status: (await entry.refresh()).message };
        },
        async action(instanceId, name, cwd, input) {
            const entry = panels.get(instanceId);
            if (!entry || entry.cwd !== cwd) throw new Error("Reopen the canvas for the active worktree before invoking actions.");
            return entry.action(name, input);
        },
        async close(instanceId) {
            const entry = panels.get(instanceId) ?? await pending.get(instanceId);
            pending.delete(instanceId);
            panels.delete(instanceId);
            if (entry) await entry.close();
        },
        async dispose() {
            for (const id of new Set([...panels.keys(), ...pending.keys()])) await this.close(id);
        },
    };
}

function proxyHeaders(headers, target) {
    // Drop forwarding metadata and cookies; this is a fixed local target, not a general-purpose proxy.
    const result = {};
    for (const name of ["accept", "accept-encoding", "content-type", "range", "if-none-match",
        "upgrade", "connection", "sec-websocket-key", "sec-websocket-version", "sec-websocket-protocol"]) {
        if (headers[name]) result[name] = headers[name];
    }
    result.host = target.host;
    if (headers.origin) result.origin = target.origin;
    return result;
}
