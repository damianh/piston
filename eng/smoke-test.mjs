// Smoke test: runs a solution through the Piston tool and checks the startup run's counts.
//
//   node eng/smoke-test.mjs --tool <piston executable or Piston.dll> --total N --failed N --skipped N
//        [--projects N] [--web-port P] [--mcp-port P] [--timeout-seconds S] -- <solution>
//
// Starts `piston daemon <solution>`, polls `piston status <solution>` until the startup run has finished, compares its
// totals with the expected ones, then stops the daemon. Exits non-zero (printing the daemon output and
// .piston/diagnostics.log) on a mismatch or timeout.

import { spawn, spawnSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { setTimeout as delay } from "node:timers/promises";

const args = process.argv.slice(2);
const separator = args.indexOf("--");
if (separator < 0 || separator === args.length - 1) {
  fail("usage: node eng/smoke-test.mjs --tool <path> --total N --failed N --skipped N [options] -- <solution>");
}

const options = {};
for (let i = 0; i < separator; i += 2) {
  options[args[i].replace(/^--/, "")] = args[i + 1];
}

const solution = resolve(args[separator + 1]);
const tool = resolve(required("tool"));
const expected = {
  total: Number(required("total")),
  failed: Number(required("failed")),
  skipped: Number(required("skipped")),
};
expected.passed = expected.total - expected.failed - expected.skipped;
const projects = options.projects === undefined ? undefined : Number(options.projects);
const timeoutMs = Number(options["timeout-seconds"] ?? 600) * 1000;
const portArgs = ["--web-port", options["web-port"] ?? "5299", "--mcp-port", options["mcp-port"] ?? "5300"];

// A framework-dependent Piston.dll runs through `dotnet`; the installed tool shim runs directly.
const [command, prefix] = tool.endsWith(".dll") ? ["dotnet", [tool]] : [tool, []];
const piston = (...rest) => [command, [...prefix, ...rest]];

let output = "";
const [daemonCommand, daemonArgs] = piston("daemon", solution, ...portArgs);
console.log(`> ${daemonCommand} ${daemonArgs.join(" ")}`);
const daemon = spawn(daemonCommand, daemonArgs, { stdio: ["ignore", "pipe", "pipe"] });
daemon.stdout.on("data", (chunk) => (output += chunk));
daemon.stderr.on("data", (chunk) => (output += chunk));
let exited = false;
daemon.on("exit", (code, signal) => {
  exited = true;
  output += `\n[daemon exited with code ${code}, signal ${signal ?? "none"}]\n`;
});

let exitCode = 1;
try {
  const result = await waitForStartupRun();
  console.log(`Startup run: ${result.line}`);
  const actual = { total: result.total, passed: result.passed, failed: result.failed, skipped: result.skipped };
  const mismatches = Object.keys(actual).filter((key) => actual[key] !== expected[key]);
  if (projects !== undefined && result.suites !== projects) {
    mismatches.push(`suites (expected ${projects}, got ${result.suites})`);
  }

  if (result.status !== "Completed") {
    mismatches.push(`status (expected Completed, got ${result.status})`);
  }

  if (mismatches.length > 0) {
    console.error(`Expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}; mismatched: ${mismatches.join(", ")}`);
    dumpDiagnostics();
  } else {
    console.log("Smoke test passed.");
    exitCode = 0;
  }
} catch (error) {
  console.error(error.message);
  dumpDiagnostics();
} finally {
  await stopDaemon();
}

process.exit(exitCode);

async function waitForStartupRun() {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (exited) {
      throw new Error("The daemon exited before the startup run finished.");
    }

    const status = runPiston("status", solution);
    const latest = /^\s*Latest\s*:\s*(.+)$/m.exec(status.stdout)?.[1];
    const active = /^\s*Active\s*:/m.test(status.stdout);
    const fields = latest?.split("|").map((field) => field.trim());
    if (fields && fields[1] === "Startup" && !active) {
      const counts = /(\d+) tests \((\d+) passed, (\d+) failed, (\d+) skipped\)/.exec(latest);
      if (!counts) {
        throw new Error(`Unrecognized status line: ${latest}`);
      }

      return {
        line: latest,
        status: fields[2],
        total: Number(counts[1]),
        passed: Number(counts[2]),
        failed: Number(counts[3]),
        skipped: Number(counts[4]),
        suites: Number(/^\s*Suites\s*:\s*(\d+)/m.exec(status.stdout)?.[1]),
      };
    }

    await delay(2000);
  }

  throw new Error(`Timed out after ${timeoutMs / 1000}s waiting for the startup run.`);
}

async function stopDaemon() {
  if (exited) {
    return;
  }

  runPiston("stop", solution);
  for (let i = 0; i < 30 && !exited; i++) {
    await delay(1000);
  }

  if (!exited) {
    daemon.kill();
  }
}

function runPiston(...rest) {
  const [cmd, cmdArgs] = piston(...rest);
  return spawnSync(cmd, cmdArgs, { encoding: "utf8", timeout: 30_000 });
}

function dumpDiagnostics() {
  console.error("── daemon output ──");
  console.error(output);
  const diagnostics = join(dirname(solution), ".piston", "diagnostics.log");
  if (existsSync(diagnostics)) {
    console.error("── .piston/diagnostics.log ──");
    console.error(readFileSync(diagnostics, "utf8"));
  }
}

function required(name) {
  return options[name] ?? fail(`missing --${name}`);
}

function fail(message) {
  console.error(message);
  process.exit(2);
}
