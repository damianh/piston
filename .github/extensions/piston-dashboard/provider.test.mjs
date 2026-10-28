import { test } from "node:test";
import assert from "node:assert/strict";
import { createServer, request } from "node:http";
import { createHash } from "node:crypto";
import { createDashboardCanvas, loopbackEndpoint, probe, rpc } from "./provider.mjs";

async function serve(t, handler, upgrade) {
    const server = createServer(handler);
    const sockets = new Set();
    server.on("connection", socket => {
        sockets.add(socket);
        socket.on("close", () => sockets.delete(socket));
    });
    if (upgrade) server.on("upgrade", upgrade);
    await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
    t.after(() => new Promise(resolve => {
        for (const socket of sockets) socket.destroy();
        server.close(resolve);
    }));
    return `http://127.0.0.1:${server.address().port}/`;
}

const connected = url => ({ state: "connected", message: "Dashboard available.", url,
    worktree: "/repo/current", processId: 123, acquiredAt: "2026-01-01T00:00:00+00:00" });

test("open, refresh, re-open and close only inspect; actions require explicit invocation", async t => {
    let runs = 0;
    const directories = [];
    const provider = createDashboardCanvas({
        inspect: async cwd => { directories.push(cwd); return connected("http://127.0.0.1:1234/"); },
        check: async state => new URL(state.url),
        invoke: async (_, method) => { runs++; return { method }; },
    });
    t.after(() => provider.dispose());
    const first = await provider.open("panel", "/repo/current");
    const second = await provider.open("panel", "/repo/current");
    assert.equal(first.url, second.url);
    assert.match(await (await fetch(first.url)).text(), /Opening this panel does not start Piston or run tests/);
    assert.equal((await provider.action("panel", "refresh", "/repo/current")).state, "connected");
    assert.equal(runs, 0);
    assert.deepEqual(await provider.action("panel", "run", "/repo/current"), { method: "engine/forceRun" });
    assert.deepEqual(await provider.action("panel", "run_failed", "/repo/current"), { method: "engine/runFailed" });
    assert.equal(runs, 2);
    assert.ok(directories.every(cwd => cwd === "/repo/current"));
    await assert.rejects(provider.action("panel", "run", "/repo/other"), /active worktree/);
    await provider.close("panel");
    assert.equal(runs, 2);
    await assert.rejects(fetch(first.url));
});

test("disconnected opens, disabled sessions and engine restarts recover without changing the shell URL", async t => {
    let state = { state: "disconnected", message: "No live Piston owner." };
    const provider = createDashboardCanvas({
        inspect: async () => state,
        check: async next => new URL(next.url),
        invoke: async () => assert.fail("No run may be invoked"),
    });
    t.after(() => provider.dispose());
    const panel = await provider.open("panel", "/repo/current");
    assert.match(await (await fetch(panel.url)).text(), /Retry connection/);
    await assert.rejects(provider.action("panel", "run", "/repo/current"), /No live/);
    state = { state: "disabled", message: "Opt in with --dashboard." };
    assert.equal((await provider.action("panel", "refresh", "/repo/current")).state, "disabled");
    state = connected("http://127.0.0.1:1234/");
    const before = await provider.action("panel", "refresh", "/repo/current");
    state = { ...state, processId: 124 };
    const after = await provider.action("panel", "refresh", "/repo/current");
    assert.notEqual(before.identity, after.identity);
    assert.equal((await provider.open("panel", "/repo/current")).url, panel.url);
});

test("HTTP proxy preserves assets and rejects cross-origin and arbitrary upstream requests", async t => {
    const received = [];
    const upstream = await serve(t, (req, res) => {
        received.push(req);
        res.writeHead(200, { "Content-Type": "application/wasm", "Content-Security-Policy": "default-src 'self'" });
        res.end("existing Blazor asset");
    });
    const provider = createDashboardCanvas({
        inspect: async () => connected(upstream), check: async state => new URL(state.url),
    });
    t.after(() => provider.dispose());
    const panel = await provider.open("panel", "/repo/current");
    const origin = new URL(panel.url).origin;
    const asset = await fetch(`${origin}/_framework/app.wasm`, { headers: { Origin: origin } });
    assert.equal(await asset.text(), "existing Blazor asset");
    assert.equal(asset.headers.get("content-security-policy"), "default-src 'self'");
    assert.equal(received[0].headers.origin, new URL(upstream).origin);
    for (const [headers, method] of [[{ Origin: "https://evil.example" }, "GET"], [{ Host: "evil.example" }, "GET"], [{}, "POST"]]) {
        const status = await new Promise((resolve, reject) => {
            const req = request(panel.url, { headers, method }, res => { res.resume(); resolve(res.statusCode); });
            req.on("error", reject); req.end();
        });
        assert.equal(status, 403);
    }
    const escaped = await new Promise((resolve, reject) => {
        const req = request(origin, { path: "//evil.example/file" }, res => { res.resume(); resolve(res.statusCode); });
        req.on("error", reject); req.end();
    });
    assert.equal(escaped, 403);
    assert.equal(received.length, 1);
});

test("health probe requires matching worktree, PID and ownership generation", async t => {
    const state = connected(await serve(t, (_, res) => {
        res.setHeader("Content-Type", "application/json");
        res.end(JSON.stringify({ worktreePath: "/repo/current", processId: 123, acquiredAt: "2026-01-01T00:00:00+00:00" }));
    }));
    assert.equal((await probe(state)).href, state.url);
    await assert.rejects(probe({ ...state, worktree: "/repo/other" }), /no longer belongs/);
    await assert.rejects(probe({ ...state, processId: 124 }), /no longer belongs/);
    await assert.rejects(probe({ ...state, acquiredAt: "old" }), /no longer belongs/);
});

test("rehydration creates a new server and resolves state from the active worktree", async t => {
    const make = () => createDashboardCanvas({ inspect: async () => ({ state: "disabled", message: "Enable --dashboard." }) });
    const old = make();
    const first = await old.open("panel", "/repo/current");
    await old.dispose();
    const resumed = make();
    t.after(() => resumed.dispose());
    const next = await resumed.open("panel", "/repo/current");
    assert.ok(next.url);
    assert.equal((await fetch(next.url)).status, 200);
    assert.notEqual(next.url, first.url);
});

test("discovery failures are visible, and concurrent opens use one server", async t => {
    const provider = createDashboardCanvas({ inspect: async () => { throw new Error("Piston not installed."); } });
    t.after(() => provider.dispose());
    const panels = await Promise.all([provider.open("panel", "/repo/current"), provider.open("panel", "/repo/current")]);
    assert.equal(panels[0].url, panels[1].url);
    assert.match(panels[0].status, /not installed/);
    await assert.rejects(provider.action("panel", "run_failed", "/repo/current"), /not installed/);
});

test("closing the panel cancels action transport without issuing engine stop", async () => {
    let actionSignal;
    const methods = [];
    const provider = createDashboardCanvas({
        inspect: async () => connected("http://127.0.0.1:1234/"),
        check: async state => new URL(state.url),
        invoke: async (_, method, { signal }) => {
            methods.push(method);
            actionSignal = signal;
            return new Promise((_, reject) => signal.addEventListener("abort",
                () => reject(new Error("Canvas closed.")), { once: true }));
        },
    });
    await provider.open("panel", "/repo/current");
    const action = provider.action("panel", "run", "/repo/current");
    const rejected = assert.rejects(action, /Canvas closed/);
    await new Promise(resolve => setImmediate(resolve));
    await provider.close("panel");
    await rejected;
    assert.equal(actionSignal.aborted, true);
    assert.deepEqual(methods, ["engine/forceRun"]);
});

test("loopback validation rejects remote, credentialed and path-bearing URLs", () => {
    for (const value of ["https://127.0.0.1:1234/", "http://evil.example:1234/", "http://127.0.0.1:1234/path",
        "http://user:pass@127.0.0.1:1234/", "http://127.0.0.1:1234/?target=x", "http://127.0.0.1:1234/#x"]) {
        assert.throws(() => loopbackEndpoint(value), /invalid/);
    }
    assert.equal(loopbackEndpoint("http://127.0.0.1:1234/").port, "1234");
});

test("RPC errors are surfaced, not reported as successful runs", async t => {
    const endpoint = await serve(t, (_, res) => { res.writeHead(503); res.end(); });
    await assert.rejects(rpc(endpoint, "engine/forceRun", { timeout: 1000 }), /connect|disconnected/);
});

test("RPC uses Piston's string request IDs and propagates protocol results/errors", async t => {
    let received;
    let error = false;
    const endpoint = await serve(t, (_, res) => res.end(), (req, socket) => {
        t.after(() => socket.destroy());
        const accept = createHash("sha1").update(req.headers["sec-websocket-key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").digest("base64");
        socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
        socket.once("data", frame => {
            const length = frame[1] & 127;
            const payload = Buffer.from(frame.subarray(6, 6 + length));
            for (let n = 0; n < payload.length; n++) payload[n] ^= frame[2 + n % 4];
            received = JSON.parse(payload.toString());
            const body = Buffer.from(JSON.stringify(error
                ? { jsonrpc: "2.0", id: received.id, error: { code: -32600, message: "No failed tests to run." } }
                : { jsonrpc: "2.0", id: received.id, result: null }));
            socket.write(Buffer.concat([Buffer.from([0x81, body.length]), body]));
        });
    });
    assert.deepEqual(await rpc(endpoint, "engine/forceRun", { timeout: 1000 }),
        { method: "engine/forceRun", result: null });
    assert.equal(received.id, "canvas-action");
    error = true;
    await assert.rejects(rpc(endpoint, "engine/runFailed", { timeout: 1000 }), /No failed tests/);
});
