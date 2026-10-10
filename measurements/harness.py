#!/usr/bin/env python3
"""Linux-first baseline instrumentation; no agents, daemon or containers launched implicitly."""

import argparse
import concurrent.futures
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import random
import re
import shutil
import signal
import statistics
import subprocess
import threading
import time
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parent
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def digest(value):
    return hashlib.sha256(value).hexdigest()


def source_hash(root):
    files = sorted(p for p in root.rglob("*") if p.is_file()
                   and not {"bin", "obj", "TestResults"}.intersection(p.relative_to(root).parts))
    return digest(b"".join(str(p.relative_to(root)).encode() + b"\0" + p.read_bytes() + b"\0"
                           for p in files))


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n")


def validate_manifest(data):
    if data.get("schema_version") != 1 or not isinstance(data.get("workloads"), dict):
        raise ValueError("Expected version 1 workloads manifest")
    for workload in data["workloads"].values():
        for key in ("solution", "source"):
            path = Path(workload[key])
            if path.is_absolute() or ".." in path.parts:
                raise ValueError("Fixture paths must be relative and contained")
        if not isinstance(workload["minimum_tests"], int) or workload["minimum_tests"] < 1:
            raise ValueError("minimum_tests must be positive")
        for scenario in workload["scenarios"].values():
            if not scenario["before"] or scenario["before"] == scenario["after"]:
                raise ValueError("Each scenario needs a real, unique edit")
            if not isinstance(scenario["failed_suffixes"], list):
                raise ValueError("failed_suffixes must be a list")
    return data


def parse_trx(directory, started_ns=0):
    results = {}
    files = sorted(directory.glob("*.trx"))
    if not files:
        raise ValueError("Missing TRX results")
    for path in files:
        if path.stat().st_mtime_ns < started_ns:
            raise ValueError("Stale TRX results")
        tree = ET.parse(path)
        definitions = {}
        for test in tree.findall(".//t:UnitTest", NS):
            method = test.find("t:TestMethod", NS)
            if method is None:
                raise ValueError("Missing test definition")
            definitions[test.attrib["id"]] = method.attrib["className"].split(",")[0]
        local = tree.findall(".//t:UnitTestResult", NS)
        counters = tree.find(".//t:Counters", NS)
        if counters is None or int(counters.attrib["total"]) != len(local):
            raise ValueError("Incomplete TRX counters")
        for result in local:
            identity = definitions[result.attrib["testId"]] + "." + result.attrib["testName"]
            # VSTest testName can already be fully qualified.
            class_name = definitions[result.attrib["testId"]]
            if result.attrib["testName"].startswith(class_name + "."):
                identity = result.attrib["testName"]
            if identity in results:
                raise ValueError("Duplicate test identity: " + identity)
            outcome = result.attrib["outcome"]
            if outcome not in ("Passed", "Failed", "NotExecuted"):
                raise ValueError("Unsupported outcome: " + outcome)
            results[identity] = outcome
    return results


def expected_results(baseline, suffixes):
    expected = dict(baseline)
    for suffix in suffixes:
        matches = [name for name in baseline if name.endswith(suffix)]
        if len(matches) != 1:
            raise ValueError("Sentinel must identify exactly one test: " + suffix)
        expected[matches[0]] = "Failed"
    return expected


def oracle(expected, observed, exit_code):
    if expected != observed:
        raise ValueError("Test identities/statuses disagree with full-scope oracle")
    expected_exit = 1 if "Failed" in expected.values() else 0
    if exit_code != expected_exit:
        raise ValueError(f"Unexpected exit code {exit_code}, expected {expected_exit}")


def wait_union(intervals):
    """Explicit blocked intervals only; not inferred from test-tool duration."""
    merged = []
    for start, end in sorted(intervals):
        if start < 0 or end < start or not math.isfinite(start + end):
            raise ValueError("Invalid blocked-wait interval")
        if merged and start <= merged[-1][1]:
            merged[-1][1] = max(end, merged[-1][1])
        else:
            merged.append([start, end])
    return sum(end - start for start, end in merged)


def paired_changes(records, candidate, seed=0):
    """Pair batch means, never treat simultaneous workers as independent pairs."""
    if candidate == "dotnet" or len({(r["workload"], r["scenario"]) for r in records}) != 1:
        raise ValueError("Pair one workload/scenario and a distinct candidate at a time")
    batches = {}
    for row in records:
        if not row["correct"]:
            raise ValueError("Correctness failures disqualify paired benefit calculation")
        value = row["edit_to_correct_result_ms"]
        if value is None or value <= 0 or not math.isfinite(value):
            raise ValueError("Positive finite latency required")
        key = (row["workload"], row["scenario"], row["batch_id"])
        batches.setdefault(key, {}).setdefault(row["condition"], []).append(value)
    changes = []
    for conditions in batches.values():
        if set(conditions) != {"dotnet", candidate}:
            raise ValueError("Each batch needs both matched conditions")
        if len(conditions["dotnet"]) != len(conditions[candidate]):
            raise ValueError("Concurrency differs between paired conditions")
        baseline = statistics.mean(conditions["dotnet"])
        compared = statistics.mean(conditions[candidate])
        changes.append({"absolute_ms": baseline - compared,
                        "relative": (baseline - compared) / baseline})
    if not changes:
        raise ValueError("No paired batches")
    relative = [change["relative"] for change in changes]
    interval = None
    if len(changes) >= 2:
        rng = random.Random(seed)
        samples = sorted(statistics.median(rng.choices(relative, k=len(relative)))
                         for _ in range(2000))
        interval = [samples[49], samples[1949]]
    return {"paired_batches": len(changes),
            "median_absolute_ms": statistics.median(c["absolute_ms"] for c in changes),
            "median_relative": statistics.median(relative),
            "bootstrap_95_percent_interval": interval, "seed": seed,
            "uncertainty_reason": None if interval else "Too few independent batches"}


def owned_pids(pid):
    """Read only descendants of the subprocess we started."""
    result = [pid]
    for current in result:
        children = Path(f"/proc/{current}/task/{current}/children")
        try:
            result.extend(int(child) for child in children.read_text().split())
        except FileNotFoundError:
            continue
    return result


def sample_owned(pid):
    ticks = os.sysconf("SC_CLK_TCK")
    page = os.sysconf("SC_PAGE_SIZE")
    cpu, rss = 0, 0
    for child in owned_pids(pid):
        try:
            fields = Path(f"/proc/{child}/stat").read_text().rsplit(")", 1)[1].split()
            cpu += (int(fields[11]) + int(fields[12])) / ticks
            rss += int(fields[21]) * page
        except FileNotFoundError:
            continue
    return {"cpu_seconds_alive": cpu, "rss_bytes_alive": rss}


def terminate_owned(process):
    # Children first; target individual PIDs, never unrelated process names/groups.
    pids = list(reversed(owned_pids(process.pid)))
    for pid in pids:
        try:
            os.kill(pid, signal.SIGKILL)
        except ProcessLookupError:
            continue
    process.wait()


def command(argv, cwd, artifacts, label, timeout, env=None):
    started = time.monotonic_ns()
    wall = time.time_ns()
    samples = []
    timed_out = False
    with (artifacts / f"{label}.log").open("w") as log:
        process = subprocess.Popen(argv, cwd=cwd, stdout=log, stderr=subprocess.STDOUT, env=env)
        try:
            while process.poll() is None:
                samples.append({"elapsed_ns": time.monotonic_ns() - started,
                                **sample_owned(process.pid)})
                if (time.monotonic_ns() - started) / 1e9 > timeout:
                    timed_out = True
                    terminate_owned(process)
                    break
                time.sleep(0.05)
        except BaseException:
            terminate_owned(process)
            raise
    result = {"command": argv, "root_pid": process.pid,
              "exit_code": process.returncode, "timed_out": timed_out,
              "elapsed_ms": (time.monotonic_ns() - started) / 1e6,
              "started_wall_ns": wall, "log": f"{label}.log",
              "cpu_seconds_observed_lower_bound": max(
                  (s["cpu_seconds_alive"] for s in samples), default=0),
              "peak_rss_bytes_sampled": max((s["rss_bytes_alive"] for s in samples), default=0)}
    write_json(artifacts / f"{label}.resources.json", samples)
    return result


def test_command(solution, results):
    return ["dotnet", "test", str(solution), "--no-restore", "--logger", "trx",
            "--results-directory", str(results), "--nologo", "--verbosity", "quiet"]


def run_trial(args, workload, scenario, output, batch_id, index, barrier):
    trial_id = f"{batch_id}-{index}"
    artifacts = output / trial_id
    artifacts.mkdir()
    workspace = artifacts / "workspace"
    shutil.copytree(ROOT / "fixtures", workspace,
                    ignore=shutil.ignore_patterns("bin", "obj", "TestResults"))
    solution = workspace / workload["solution"]
    source = workspace / workload["source"]
    env = dict(os.environ, MEASUREMENT_TRIAL_ID=trial_id,
               DOTNET_CLI_TELEMETRY_OPTOUT="1")
    events = []
    origin = time.monotonic_ns()

    def event(kind, **details):
        events.append({"schema_version": 1, "experiment_id": output.name, "trial_id": trial_id,
                       "batch_id": batch_id, "session_id": None, "source": "scripted",
                       "type": kind, "utc": datetime.now(timezone.utc).isoformat(),
                       "elapsed_ns": time.monotonic_ns() - origin, **details})

    record = {"schema_version": 1, "experiment_id": output.name, "trial_id": trial_id,
              "batch_id": batch_id, "condition": "dotnet", "workload": args.workload,
              "scenario": args.scenario, "correct": False, "timed_out": False,
              "edit_to_correct_result_ms": None, "agent_blocked_wait_ms": None,
              "pre_existing_failure_turns": None, "total_turns": None,
              "agent_metric_reason": "Scripted commands are not agent sessions",
              "incidents": [], "stage_durations_ms": None, "container_stats": None,
              "stage_duration_reason": "Command-level timing only; no build/test stage instrumentation",
              "container_stats_reason": "Not collected by baseline runner",
              "artifact_directory": str(artifacts)}
    try:
        base_hash = source_hash(workspace)
        if "seed_before" in scenario:
            initial = source.read_text()
            if initial.count(scenario["seed_before"]) != 1:
                raise ValueError("Baseline seed anchor is not unique")
            source.write_text(initial.replace(scenario["seed_before"], scenario["seed_after"]))
        record["baseline_hash"] = source_hash(workspace)
        setup = command(["dotnet", "restore", str(solution), "--locked-mode", "--verbosity", "quiet"],
                        workspace, artifacts, "restore", args.timeout, env)
        record["restore"] = setup
        record["timed_out"] = setup["timed_out"]
        if setup["timed_out"] or setup["exit_code"]:
            raise ValueError("Restore failed; see restore.log")
        baseline_dir = artifacts / "baseline"
        baseline_dir.mkdir()
        baseline_run = command(test_command(solution, baseline_dir), workspace, artifacts,
                               "baseline", args.timeout, env)
        record["baseline_setup"] = baseline_run
        record["timed_out"] = baseline_run["timed_out"]
        if baseline_run["timed_out"]:
            raise ValueError("Baseline timed out")
        baseline = parse_trx(baseline_dir, baseline_run["started_wall_ns"])
        if len(baseline) < workload["minimum_tests"]:
            raise ValueError("Baseline scope is incomplete")
        clean = {identity: "Passed" for identity in baseline}
        baseline_expected = expected_results(clean, scenario.get("baseline_failed_suffixes", []))
        oracle(baseline_expected, baseline, baseline_run["exit_code"])
        record["baseline_tests"] = baseline
        original = source.read_text()
        if original.count(scenario["before"]) != 1:
            raise ValueError("Edit anchor is not unique")
        edited = original.replace(scenario["before"], scenario["after"])
        if scenario.get("repair"):
            source.write_text(edited)
            preparation = artifacts / "repair-preparation"
            preparation.mkdir()
            regression = command(test_command(solution, preparation), workspace, artifacts,
                                 "repair-preparation", args.timeout, env)
            record["timed_out"] = regression["timed_out"]
            if regression["timed_out"]:
                raise ValueError("Repair preparation timed out")
            observed = parse_trx(preparation, regression["started_wall_ns"])
            suffixes = workload["scenarios"]["regression"]["failed_suffixes"]
            oracle(expected_results(baseline, suffixes), observed, regression["exit_code"])
            record["repair_preparation"] = regression
            edited = original
        record["base_hash"] = base_hash
        record["pre_edit_hash"] = source_hash(workspace)
        record["oracle_discovery_hash"] = digest(json.dumps(sorted(baseline)).encode())
        expected = expected_results(baseline, scenario["failed_suffixes"])
        previous_source = source.read_text()
        event("queued")
        barrier.wait(timeout=args.timeout)
        source.write_text(edited)
        edit_time = time.monotonic_ns()
        event("edit-applied", patch_hash=digest((previous_source + "\0" + edited).encode()))
        edit_hash = source_hash(workspace)
        results_dir = artifacts / "edited"
        results_dir.mkdir()
        event("command-started")
        measured = command(test_command(solution, results_dir), workspace, artifacts,
                           "edited", args.timeout, env)
        available = time.monotonic_ns()
        event("result-available", exit_code=measured["exit_code"])
        record["execution"] = measured
        record["timed_out"] = measured["timed_out"]
        record["edit_hash"] = edit_hash
        if measured["timed_out"]:
            raise ValueError("Measured command timed out")
        if source_hash(workspace) != edit_hash:
            raise ValueError("Source changed during measured execution")
        if "diagnostic" in scenario:
            text = (artifacts / "edited.log").read_text()
            if measured["exit_code"] == 0 or not re.search(
                    rf"\berror {re.escape(scenario['diagnostic'])}\b", text):
                raise ValueError("Expected compile diagnostics absent")
            if list(results_dir.glob("*.trx")):
                raise ValueError("Compile-error trial unexpectedly produced test results")
            record["oracle_kind"] = "compile-diagnostic"
        else:
            observed = parse_trx(results_dir, measured["started_wall_ns"])
            oracle(expected, observed, measured["exit_code"])
            record["expected_tests"] = expected
            record["observed_tests"] = observed
            record["oracle_kind"] = "full-test-scope"
        record["correct"] = True
        record["edit_to_correct_result_ms"] = (available - edit_time) / 1e6
        event("oracle-checked", correct=True)
    except (ValueError, OSError, ET.ParseError, KeyError, threading.BrokenBarrierError) as error:
        barrier.abort()
        record["error"] = str(error)
        if record["timed_out"]:
            record["incidents"].append({"kind": "timeout", "attribution": "unclassified",
                                        "evidence": str(error)})
            event("timeout", error=str(error))
        event("incident", error=str(error))
    finally:
        event("cleanup", status="workspace retained; subprocesses completed or terminated")
        write_json(artifacts / "trial.json", record)
        (artifacts / "events.jsonl").write_text(
            "".join(json.dumps(e) + "\n" for e in events))
    return record


def summarize(records):
    groups = {}
    for record in records:
        key = (record["condition"], record["workload"], record["scenario"])
        groups.setdefault(key, []).append(record)
    output = []
    for (condition, workload, scenario), rows in groups.items():
        values = sorted(r["edit_to_correct_result_ms"] for r in rows
                        if r["correct"] and r["edit_to_correct_result_ms"] is not None)
        output.append({"condition": condition, "workload": workload, "scenario": scenario,
                       "attempted": len(rows), "valid": len(values),
                       "invalid": len(rows) - len(values),
                       "timeouts": sum(r["timed_out"] for r in rows),
                       "invalid_reasons": [r.get("error", "No valid latency") for r in rows
                                           if not r["correct"]],
                       "incidents": sum(len(r["incidents"]) for r in rows),
                       "batches": len({r["batch_id"] for r in rows}),
                       "median_ms": statistics.median(values) if values else None,
                       "p95_ms": values[math.ceil(0.95 * len(values)) - 1] if values else None})
    return {"schema_version": 1, "G0": "pending", "groups": output,
            "paired_improvement": None, "uncertainty": None,
            "reason": "Baseline instrumentation only; no validated backend or real-agent comparison"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workload", choices=["unit", "integration"], default="unit")
    parser.add_argument("--scenario", default="baseline")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--concurrency", type=int, choices=[1, 2, 4], default=1)
    parser.add_argument("--repetitions", type=int, default=1)
    parser.add_argument("--timeout", type=float, default=180)
    parser.add_argument("--approve-campaign", action="store_true")
    parser.add_argument("--approve-containers", action="store_true")
    args = parser.parse_args()
    data = validate_manifest(json.loads((ROOT / "scenarios.json").read_text()))
    workload = data["workloads"][args.workload]
    if args.scenario not in workload["scenarios"]:
        parser.error("Unknown scenario for selected workload")
    if not math.isfinite(args.timeout) or args.timeout <= 0 or args.repetitions < 1:
        parser.error("timeout/repetitions must be positive and finite")
    if (args.repetitions > 1 or args.concurrency > 1) and not args.approve_campaign:
        parser.error("Repeated/concurrent runs require explicit --approve-campaign")
    if args.workload == "integration":
        if not args.approve_containers:
            parser.error("Integration execution requires explicit --approve-containers")
        if not re.fullmatch(r"postgres@sha256:[0-9a-f]{64}",
                            os.environ.get("MEASUREMENT_POSTGRES_IMAGE", "")):
            parser.error("Set MEASUREMENT_POSTGRES_IMAGE to an approved immutable digest")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    manifest = {"schema_version": 1, "experiment_id": output.name, "source": "scripted",
                "workload": args.workload, "scenario": args.scenario, "conditions": ["dotnet"],
                "concurrency": args.concurrency, "repetitions": args.repetitions,
                "timeout_seconds": args.timeout, "cache_policy": "restored and baseline-warmed",
                "fixture_hash": source_hash(ROOT / "fixtures"), "test_scope": "full solution",
                "repository_revision": subprocess.check_output(
                    ["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
                "scenario_definition": workload["scenarios"][args.scenario],
                "sdk": subprocess.check_output(["dotnet", "--version"],
                                               cwd=ROOT / "fixtures", text=True).strip(),
                "os": platform.platform(), "cpu_count": os.cpu_count(),
                "host_memory": Path("/proc/meminfo").read_text(),
                "host_pressure_context": {
                    kind: Path(f"/proc/pressure/{kind}").read_text()
                    for kind in ("cpu", "memory", "io")},
                "runner_cgroup_membership": Path("/proc/self/cgroup").read_text(),
                "image": os.environ.get("MEASUREMENT_POSTGRES_IMAGE"),
                "telemetry_cadence_ms": 50,
                "permissions": {"campaign": args.approve_campaign,
                                "containers": args.approve_containers},
                "comparator": {"status": "blocked",
                               "reason": "Current MCP lacks generation-bound full-scope result evidence"}}
    write_json(output / "manifest.json", manifest)
    records = []
    for repetition in range(args.repetitions):
        barrier = threading.Barrier(args.concurrency)
        batch = f"batch-{repetition}"
        with concurrent.futures.ThreadPoolExecutor(max_workers=args.concurrency) as executor:
            futures = [executor.submit(run_trial, args, workload, workload["scenarios"][args.scenario],
                                       output, batch, index, barrier)
                       for index in range(args.concurrency)]
            records.extend(future.result() for future in futures)
    (output / "trials.jsonl").write_text("".join(json.dumps(r) + "\n" for r in records))
    write_json(output / "summary.json", summarize(records))
    print(json.dumps(summarize(records), indent=2))
    return 0 if all(r["correct"] for r in records) else 1


if __name__ == "__main__":
    raise SystemExit(main())
