// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { createServer, request } from "node:http";
import { readFile } from "node:fs/promises";

const execute = promisify(execFile);
const assets = new Map(await Promise.all(["shell.html", "shell.js", "shell.css"].map(async name =>
    [name, await readFile(new URL(name, import.meta.url))])));

export function loopbackEndpoint(value) {
    const url = new URL(value);
    if (url.protocol !== "http:" || !["localhost", "127.0.0.1", "[::1]"].includes(url.hostname)
        || url.username || url.password || url.pathname !== "/" || url.search || url.hash) {
        throw new Error("Piston advertised an invalid loopback dashboard endpoint.");
    }
    return url;
}

export async function discover(cwd) {
    try {
        const { stdout } = await execute("piston", ["dashboard", "--json"], {
            cwd, timeout: 5000, maxBuffer: 64 * 1024, windowsHide: true,
        });
        const state = JSON.parse(stdout);
        if (!["connected", "disconnected", "disabled", "error"].includes(state.state) || typeof state.message !== "string") {
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

export function createDashboardCanvas({ inspect = discover, check = probe, invoke = rpc } = {}) {
    const panels = new Map();
    const pending = new Map();

    async function create(cwd) {
        let state = { state: "disconnected", message: "Inspecting the current worktree..." };
        let target;
        let refreshing;
        let checkedAt = 0;
        let closed = false;
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
                    state = { ...next, identity };
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
            if (!allowed(req) || !["GET", "HEAD"].includes(req.method)) {
                res.writeHead(403); res.end("Cross-origin or unsupported request refused."); return;
            }
            res.setHeader("X-Content-Type-Options", "nosniff");
            res.setHeader("Referrer-Policy", "no-referrer");
            res.setHeader("Cache-Control", "no-store");
            const path = req.url.split("?")[0];
            if (path === "/__piston/state") {
                res.setHeader("Content-Type", "application/json");
                res.end(JSON.stringify(await refresh())); return;
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
            async action(name) {
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
        async open(instanceId, cwd) {
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
            await entry.refresh(true);
            return { url: entry.url, title: "Piston", status: (await entry.refresh()).message };
        },
        async action(instanceId, name, cwd) {
            const entry = panels.get(instanceId);
            if (!entry || entry.cwd !== cwd) throw new Error("Reopen the canvas for the active worktree before invoking actions.");
            return entry.action(name);
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
