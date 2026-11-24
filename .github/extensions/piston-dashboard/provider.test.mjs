import { test } from "node:test";
import assert from "node:assert/strict";
import { createServer, request } from "node:http";
import { createHash } from "node:crypto";
import { EventEmitter } from "node:events";
import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { mkdtemp, mkdir, realpath, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { changesQuery, createDashboardCanvas, discover, discoverSolutions, loopbackEndpoint, normalizeWorkingDirectory, openQuery, probe, rpc, startDaemon } from "./provider.mjs";

test("Windows worktree paths use ordinary spelling for CLI discovery", () => {
    assert.equal(normalizeWorkingDirectory("\\\\?\\D:\\repo\\wt", "win32"), "D:\\repo\\wt");
    assert.equal(normalizeWorkingDirectory("\\\\?\\UNC\\server\\share\\wt", "win32"), "\\\\server\\share\\wt");
    for (const special of ["\\\\?\\D:\\repo\\dir.", "\\\\?\\D:\\repo\\NUL", "\\\\?\\Volume{abc}\\repo"]) {
        assert.equal(normalizeWorkingDirectory(special, "win32"), special);
    }
    assert.equal(normalizeWorkingDirectory("\\\\?\\D:\\repo\\wt", "linux"), "\\\\?\\D:\\repo\\wt");
});

test("discovery preserves validated CLI error responses from exit code 1", async () => {
    const state = { state: "error", message: "The owner advertised an invalid dashboard endpoint." };
    const result = await discover("/repo/current", async (command, args, options) => {
        assert.equal(command, "piston");
        assert.deepEqual(args, ["dashboard", "--json"]);
        assert.equal(options.cwd, "/repo/current");
        throw Object.assign(new Error("Command failed"), { code: 1, stdout: JSON.stringify(state) });
    });
    assert.deepEqual(result, state);
});

test("discovery rejects launch failures, malformed output and unexpected exit states", async () => {
    for (const error of [
        Object.assign(new Error("Missing executable"), { code: "ENOENT" }),
        Object.assign(new Error("Timed out"), { killed: true }),
        Object.assign(new Error("Command failed"), { code: 2, stdout: '{"state":"error","message":"bad"}' }),
        Object.assign(new Error("Command failed"), { code: 1, stdout: "not JSON" }),
        Object.assign(new Error("Command failed"), { code: 1, stdout: '{"state":"error"}' }),
        Object.assign(new Error("Command failed"), { code: 1, stdout: '{"state":"connected","message":"ok"}' }),
    ]) {
        await assert.rejects(discover("/repo/current", async () => { throw error; }), /Could not inspect Piston/);
    }
    assert.deepEqual(await discover("/repo/current", async () => ({
        stdout: '{"state":"disabled","message":"Opt in."}',
    })), { state: "disabled", message: "Opt in." });
});

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

test("solution discovery uses current tracked and untracked worktree files, excluding ignored and deleted files", async t => {
    const root = await mkdtemp(path.join(tmpdir(), "piston-solutions-test-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const git = promisify(execFile);
    await git("git", ["init", "-q"], { cwd: root });
    await mkdir(path.join(root, "apps"));
    await writeFile(path.join(root, ".gitignore"), "ignored/\n");
    await writeFile(path.join(root, "Root.sln"), "");
    await writeFile(path.join(root, "removed.slnx"), "");
    await git("git", ["add", "Root.sln", "removed.slnx"], { cwd: root });
    await rm(path.join(root, "removed.slnx"));
    await writeFile(path.join(root, "apps", "Nested.SLNX"), "");
    await writeFile(path.join(root, "apps", "Filter.slnf"), "");
    await mkdir(path.join(root, "ignored"));
    await writeFile(path.join(root, "ignored", "ignored.sln"), "");
    assert.deepEqual(await discoverSolutions(root, root), [
        path.join("apps", "Filter.slnf"), path.join("apps", "Nested.SLNX"), "Root.sln",
    ]);
    assert.deepEqual(await discoverSolutions(root, path.join(root, "apps")), [
        "Filter.slnf", "Nested.SLNX", path.join("..", "Root.sln"),
    ].sort((a, b) => a.localeCompare(b)));
});

test("solution discovery failures are visible in the canvas", async t => {
    const root = await mkdtemp(path.join(tmpdir(), "piston-no-git-test-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const provider = createDashboardCanvas({
        inspect: async () => ({ state: "disconnected", message: "No owner.", worktree: root }),
    });
    t.after(() => provider.dispose());
    const panel = await provider.open("panel", root);
    const response = await fetch(new URL("solutions", panel.url));
    assert.equal(response.status, 500);
    assert.match((await response.json()).error, /Could not discover solutions/);
});

test("daemon startup validates the selected worktree, launches detached and checks its owner", async t => {
    const root = await mkdtemp(path.join(tmpdir(), "piston-canvas-test-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const worktree = path.join(root, "worktree");
    await mkdir(worktree);
    await writeFile(path.join(worktree, "App.slnx"), "<Solution />");
    await writeFile(path.join(root, "other.slnx"), "<Solution />");
    const selected = await realpath(path.join(worktree, "App.slnx"));
    let detached = true;
    const launch = (command, args, options) => {
        assert.equal(command, "piston");
        assert.deepEqual(args, ["daemon", selected, "--no-stdio", "--config-directory", worktree]);
        assert.equal(options.detached, detached);
        assert.equal(options.windowsHide, true);
        assert.equal(options.cwd, worktree);
        const child = new EventEmitter();
        child.pid = 42;
        child.unref = () => {};
        return child;
    };
    const state = { ...connected("http://127.0.0.1:1234/"), processId: 42, worktree };
    let checks = 0;
    assert.equal(await startDaemon(worktree, "App.slnx", worktree, async () => state,
        async next => {
            assert.equal(next, state);
            if (checks++ === 0) throw new Error("Not ready yet.");
        }, launch, root, "linux"), state);
    assert.equal(checks, 2);
    // A detached Windows daemon has no console, so its child processes would open visible windows.
    detached = false;
    assert.equal(await startDaemon(worktree, "App.slnx", worktree, async () => state,
        async () => {}, launch, root, "win32"), state);
    await assert.rejects(startDaemon(worktree, path.join(root, "other.slnx"), worktree,
        async () => state, async () => {}, launch, root), /inside this worktree/);
    await assert.rejects(startDaemon(worktree, "App.slnx", worktree,
        async () => ({ ...state, processId: 43 }), async () => {}, launch, root, "win32"), /Another Piston owner/);
    await assert.rejects(startDaemon(worktree, "App.slnx", worktree,
        async () => ({ state: "disconnected" }), async () => {}, () => {
            const child = new EventEmitter();
            child.unref = () => {};
            setImmediate(() => child.emit("error", new Error("Cannot launch Piston.")));
            return child;
        }, root), /Cannot launch Piston/);
});

test("daemon startup rejects solutions in a different canonical worktree", async t => {
    const root = await mkdtemp(path.join(tmpdir(), "piston-canvas-identity-test-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const worktree = path.join(root, "repo");
    const nestedWorktree = path.join(worktree, "nested");
    const plainWorktree = path.join(root, "plain");
    await mkdir(path.join(worktree, ".git"), { recursive: true });
    await mkdir(path.join(worktree, "apps"), { recursive: true });
    await mkdir(nestedWorktree, { recursive: true });
    await writeFile(path.join(nestedWorktree, ".git"), "gitdir: ../.git/worktrees/nested\n");
    await writeFile(path.join(worktree, "apps", "Valid.slnx"), "<Solution />");
    await writeFile(path.join(nestedWorktree, "Other.slnx"), "<Solution />");
    await mkdir(path.join(plainWorktree, "nested"), { recursive: true });
    await writeFile(path.join(plainWorktree, "nested", "Other.slnx"), "<Solution />");

    let launches = 0;
    let processId;
    const launch = () => {
        launches++;
        const child = new EventEmitter();
        processId = child.pid = 40 + launches;
        child.unref = () => {};
        return child;
    };
    const inspect = async () => ({ ...connected("http://127.0.0.1:1234/"), processId, worktree });
    await startDaemon(worktree, path.join("apps", "Valid.slnx"), worktree, inspect, async () => {}, launch, root);
    assert.equal(launches, 1);

    await assert.rejects(startDaemon(worktree, path.join("nested", "Other.slnx"), worktree,
        inspect, async () => {}, launch, root), /canonical worktree/);
    await assert.rejects(startDaemon(plainWorktree, path.join("nested", "Other.slnx"), plainWorktree,
        inspect, async () => {}, launch, root), /canonical worktree/);
    assert.equal(launches, 1);
});

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
    assert.match(await (await fetch(first.url)).text(), /Starting Piston builds and runs tests immediately/);
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

test("start requires an explicit same-origin POST and attaches to the started owner", async t => {
    let state = { state: "disconnected", message: "No live Piston owner.", worktree: "/repo/current", solution: "/repo/current/App.slnx" };
    const calls = [];
    const provider = createDashboardCanvas({
        inspect: async () => state,
        check: async next => new URL(next.url),
        start: async (cwd, solution, worktree) => {
            calls.push({ cwd, solution, worktree });
            state = connected("http://127.0.0.1:1234/");
        },
    });
    t.after(() => provider.dispose());
    const panel = await provider.open("panel", "/repo/current");
    const origin = new URL(panel.url).origin;
    assert.equal(calls.length, 0);
    const endpoint = `${origin}/__piston/start`;
    const body = JSON.stringify({ solution: "App.slnx" });
    for (const headers of [
        { "Content-Type": "application/json" },
        { Origin: "https://evil.example", "Content-Type": "application/json" },
        { Origin: origin, "Content-Type": "text/plain" },
    ]) {
        assert.equal((await fetch(endpoint, { method: "POST", headers, body })).status, 403);
    }
    const response = await fetch(endpoint, { method: "POST", headers: { Origin: origin, "Content-Type": "application/json" }, body });
    assert.equal(response.status, 200);
    assert.equal((await response.json()).state, "connected");
    assert.deepEqual(calls, [{ cwd: "/repo/current", solution: "App.slnx", worktree: "/repo/current" }]);
    assert.equal((await fetch(endpoint, { method: "POST", headers: { Origin: origin, "Content-Type": "application/json" }, body })).status, 400);
    assert.equal(calls.length, 1);
});

test("start reports failures and never starts when a dashboard-disabled owner exists", async t => {
    let state = { state: "disconnected", message: "No owner.", worktree: "/repo/current" };
    let calls = 0;
    const provider = createDashboardCanvas({
        inspect: async () => state,
        start: async () => { calls++; throw new Error("Port is occupied."); },
    });
    t.after(() => provider.dispose());
    const panel = await provider.open("panel", "/repo/current");
    const origin = new URL(panel.url).origin;
    const post = () => fetch(`${origin}/__piston/start`, {
        method: "POST", headers: { Origin: origin, "Content-Type": "application/json" }, body: JSON.stringify({ solution: "App.slnx" }),
    });
    const failure = await post();
    assert.equal(failure.status, 400);
    assert.match((await failure.json()).error, /Port is occupied/);
    state = { state: "disabled", message: "Dashboard disabled." };
    const disabled = await post();
    assert.equal(disabled.status, 400);
    assert.match((await disabled.json()).error, /has not published a dashboard/);
    assert.equal(calls, 1);
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
    const disabled = await provider.action("panel", "refresh", "/repo/current");
    assert.equal(disabled.state, "disabled");
    assert.match(disabled.message, /upgrade Piston and restart/);
    assert.doesNotMatch(disabled.message, /Opt in/);
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
        res.writeHead(200, {
            "Content-Type": "application/wasm", "Content-Security-Policy": "default-src 'self'",
            "X-Content-Type-Options": "upstream", "Referrer-Policy": "unsafe-url", "Cache-Control": "public, max-age=3600",
        });
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
    assert.equal(asset.headers.get("content-type"), "application/wasm");
    assert.equal(asset.headers.get("x-content-type-options"), "nosniff");
    assert.equal(asset.headers.get("referrer-policy"), "no-referrer");
    assert.equal(asset.headers.get("cache-control"), "no-store");
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
    const make = () => createDashboardCanvas({ inspect: async () => ({ state: "disabled", message: "Upgrade and restart the owner." }) });
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

test("changes queries validate base and ref and never accept option-like refs", () => {
    assert.equal(changesQuery(), "tab=coverage&mode=changes&base=working");
    assert.equal(changesQuery({ baseRef: "main" }), "tab=coverage&mode=changes&base=branch&ref=main");
    assert.equal(changesQuery({ base: "working", baseRef: "main" }), "tab=coverage&mode=changes&base=working");
    assert.equal(changesQuery({ base: "range", baseRef: "HEAD~3..HEAD" }),
        "tab=coverage&mode=changes&base=range&ref=HEAD%7E3..HEAD");
    for (const input of [{ base: "staged" }, { base: "range" }, { baseRef: "--output=x" }, { baseRef: "a b" },
        { baseRef: "x".repeat(201) }, { baseRef: 3 }]) {
        assert.throws(() => changesQuery(input), /base|range|baseRef/);
    }
    assert.equal(openQuery(undefined), undefined);
    assert.equal(openQuery({}), undefined);
    assert.equal(openQuery({ view: "dashboard" }), "");
    assert.equal(openQuery({ view: "changes" }), "tab=coverage&mode=changes&base=working");
    assert.equal(openQuery({ base: "branch" }), "tab=coverage&mode=changes&base=branch");
    assert.throws(() => openQuery({ view: "dashboard", base: "branch" }), /only to the changes view/);
    assert.throws(() => openQuery({ view: "tests" }), /view must be/);
    assert.throws(() => openQuery([]), /object/);
});

test("open input and show_changes navigate the panel without running tests", async t => {
    let runs = 0;
    const provider = createDashboardCanvas({
        inspect: async () => connected("http://127.0.0.1:1234/"),
        check: async state => new URL(state.url),
        invoke: async () => { runs++; },
    });
    t.after(() => provider.dispose());
    const state = async url => (await fetch(new URL("state", url))).json();
    const { url } = await provider.open("panel", "/repo/current");
    assert.deepEqual((await state(url)).navigation, { seq: 0, query: "" });
    await provider.open("panel", "/repo/current", { view: "changes", base: "branch", baseRef: "main" });
    assert.deepEqual((await state(url)).navigation,
        { seq: 1, query: "tab=coverage&mode=changes&base=branch&ref=main" });
    await provider.open("panel", "/repo/current", {});
    assert.equal((await state(url)).navigation.seq, 1);
    const shown = await provider.action("panel", "show_changes", "/repo/current", { base: "working" });
    assert.equal(shown.shown, "?tab=coverage&mode=changes&base=working");
    assert.deepEqual((await state(url)).navigation, { seq: 2, query: "tab=coverage&mode=changes&base=working" });
    await assert.rejects(provider.action("panel", "show_changes", "/repo/current", { baseRef: "-x" }), /baseRef/);
    await assert.rejects(provider.open("panel", "/repo/current", { view: "changes", base: "nope" }), /base must be/);
    assert.equal((await state(url)).navigation.seq, 2);
    assert.equal(runs, 0);
});
