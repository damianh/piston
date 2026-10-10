import json
import concurrent.futures
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import harness


class HarnessTests(unittest.TestCase):
    def test_manifest(self):
        data = json.loads((harness.ROOT / "scenarios.json").read_text())
        self.assertEqual(1, harness.validate_manifest(data)["schema_version"])
        data["workloads"]["unit"]["source"] = "../escape.cs"
        with self.assertRaises(ValueError):
            harness.validate_manifest(data)

    def test_oracle_rejects_scope_status_and_exit_mismatch(self):
        expected = {"PriceTests.Boundary": "Passed", "PriceTests.Existing": "Failed"}
        harness.oracle(expected, dict(expected), 1)
        for observed, code in [({}, 1), (dict(expected), 0),
                               ({"PriceTests.Boundary": "Failed"}, 1)]:
            with self.assertRaises(ValueError):
                harness.oracle(expected, observed, code)

    def test_missing_and_stale_results(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            with self.assertRaises(ValueError):
                harness.parse_trx(path)
            (path / "test.trx").write_text("<invalid />")
            with self.assertRaises(ValueError):
                harness.parse_trx(path, started_ns=2**63)

    def test_trx_identity_and_counters(self):
        template = '''<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <TestDefinitions><UnitTest id="1"><TestMethod className="Tests.PriceTests, Tests"/></UnitTest></TestDefinitions>
          <Results>{results}</Results><ResultSummary><Counters total="{count}"/></ResultSummary>
        </TestRun>'''
        result = '<UnitTestResult testId="1" testName="Tests.PriceTests.Boundary" outcome="Passed"/>'
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            file = path / "test.trx"
            file.write_text(template.format(results=result, count=1))
            self.assertEqual({"Tests.PriceTests.Boundary": "Passed"}, harness.parse_trx(path))
            file.write_text(template.format(results=result * 2, count=2))
            with self.assertRaises(ValueError):
                harness.parse_trx(path)
            file.write_text(template.format(results=result, count=2))
            with self.assertRaises(ValueError):
                harness.parse_trx(path)

    def test_unique_sentinel(self):
        baseline = {"Tests.PriceTests.Boundary": "Passed"}
        self.assertEqual({"Tests.PriceTests.Boundary": "Failed"},
                         harness.expected_results(baseline, ["PriceTests.Boundary"]))
        with self.assertRaises(ValueError):
            harness.expected_results(baseline, ["Missing"])

    def test_wait_union(self):
        self.assertEqual(10, harness.wait_union([[0, 5], [3, 8], [10, 12]]))
        for intervals in ([[3, 2]], [[-1, 2]], [[0, float("inf")]]):
            with self.assertRaises(ValueError):
                harness.wait_union(intervals)

    def test_paired_batch_reduction(self):
        rows = []
        for batch in ("one", "two"):
            for condition, value in (("dotnet", 100), ("candidate", 70)):
                for _ in range(2):
                    rows.append({"condition": condition, "workload": "unit", "scenario": "repair",
                                 "batch_id": batch, "correct": True,
                                 "edit_to_correct_result_ms": value})
        reduction = harness.paired_changes(rows, "candidate", seed=42)
        self.assertEqual(2, reduction["paired_batches"])
        self.assertEqual(0.3, reduction["median_relative"])
        self.assertEqual([0.3, 0.3], reduction["bootstrap_95_percent_interval"])
        with self.assertRaises(ValueError):
            harness.paired_changes(rows[:-1], "candidate")
        rows[0]["correct"] = False
        with self.assertRaises(ValueError):
            harness.paired_changes(rows, "candidate")

    def test_execution_permission_guards(self):
        for options in (["--workload", "integration"],
                        ["--concurrency", "2"], ["--repetitions", "2"],
                        ["--timeout", "nan"]):
            with tempfile.TemporaryDirectory() as directory:
                output = Path(directory) / "not-created"
                result = subprocess.run(
                    [sys.executable, str(harness.ROOT / "harness.py"),
                     "--output", str(output), *options], capture_output=True, text=True)
                self.assertEqual(2, result.returncode)
                self.assertFalse(output.exists())

    def test_concurrent_workspace_isolation_without_dotnet_execution(self):
        workload = json.loads((harness.ROOT / "scenarios.json").read_text())["workloads"]["unit"]
        workload["minimum_tests"] = 1
        args = SimpleNamespace(workload="unit", scenario="baseline", timeout=2)

        def fake_command(argv, cwd, artifacts, label, timeout, env):
            if "--results-directory" in argv:
                directory = Path(argv[argv.index("--results-directory") + 1])
                (directory / "fake.trx").write_text('''
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <TestDefinitions><UnitTest id="1"><TestMethod className="Tests"/></UnitTest></TestDefinitions>
                  <Results><UnitTestResult testId="1" testName="One" outcome="Passed"/></Results>
                  <ResultSummary><Counters total="1"/></ResultSummary>
                </TestRun>''')
            return {"exit_code": 0, "timed_out": False, "started_wall_ns": 0}

        with tempfile.TemporaryDirectory() as directory, patch("harness.command", fake_command):
            output = Path(directory)
            barrier = threading.Barrier(2)
            with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
                futures = [executor.submit(harness.run_trial, args, workload,
                                           workload["scenarios"]["baseline"], output,
                                           "batch", index, barrier) for index in range(2)]
                records = [future.result() for future in futures]
            self.assertTrue(all(record["correct"] for record in records))
            self.assertEqual(2, len({record["artifact_directory"] for record in records}))
            for record in records:
                self.assertNotEqual(record["pre_edit_hash"], record["edit_hash"])
                events = [json.loads(line) for line in
                          (Path(record["artifact_directory"]) / "events.jsonl").read_text().splitlines()]
                elapsed = [event["elapsed_ns"] for event in events]
                self.assertEqual(sorted(elapsed), elapsed)

    def test_nonzero_and_timeout(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            result = harness.command([sys.executable, "-c", "raise SystemExit(7)"],
                                     path, path, "exit", 2)
            self.assertEqual(7, result["exit_code"])
            result = harness.command([sys.executable, "-c", "import time; time.sleep(3)"],
                                     path, path, "timeout", 0.1)
            self.assertTrue(result["timed_out"])
            self.assertNotEqual(0, result["exit_code"])

    def test_summary_keeps_failures_and_null_benefit(self):
        common = {"condition": "dotnet", "workload": "unit", "scenario": "repair",
                  "batch_id": "one", "incidents": [], "timed_out": False}
        rows = [dict(common, correct=True, edit_to_correct_result_ms=12),
                dict(common, correct=False, edit_to_correct_result_ms=None)]
        summary = harness.summarize(rows)
        self.assertEqual("pending", summary["G0"])
        self.assertIsNone(summary["paired_improvement"])
        self.assertEqual(1, summary["groups"][0]["invalid"])
        self.assertEqual(12, summary["groups"][0]["median_ms"])

    def test_container_api_requires_local_socket_and_exact_label(self):
        for host in ("", "tcp://localhost:2375", "unix://remote/socket"):
            with patch.dict("os.environ", {"DOCKER_HOST": host}):
                with self.assertRaises(ValueError):
                    harness.ContainerAPI()
        with patch.dict("os.environ", {"DOCKER_HOST": "unix:///local/socket"}):
            api = harness.ContainerAPI()
        with patch.object(api, "request", return_value=[
                {"Id": "unrelated", "Labels": {"piston.measurement.trial": "other"}}]):
            with self.assertRaises(harness.ContainerAPIError):
                api.containers("trial")

    def test_container_samples_and_cleanup_are_label_scoped(self):
        with patch.dict("os.environ", {"DOCKER_HOST": "unix:///local/socket"}):
            observer = harness.ContainerObserver("trial")
        row = {"Id": "owned", "State": "running",
               "Labels": {"piston.measurement.trial": "trial"}}
        details = {"Config": {"Labels": row["Labels"]}, "Image": "sha256:image",
                   "HostConfig": {"Memory": 512 * 1024**2, "NanoCpus": 1_000_000_000}}

        def request(method, path):
            if path.endswith("/stats?stream=false"):
                observer.stop.set()
                return {"cpu_stats": {"cpu_usage": {"total_usage": 123}},
                        "memory_stats": {"usage": 456, "limit": 512 * 1024**2}}
            return details

        observer.phase = "edited"
        with patch.object(observer.api, "containers", return_value=[row]), \
                patch.object(observer.api, "request", side_effect=request):
            observer.observe()
        self.assertEqual(123, observer.samples[0]["cpu_total_ns"])
        self.assertEqual("edited", observer.samples[0]["phase"])
        observer.thread.start()
        with patch.object(observer.api, "containers", side_effect=[[row], []]), \
                patch.object(observer.api, "request", return_value=details) as calls:
            result = observer.finish()
        self.assertTrue(result["confirmed"])
        self.assertEqual(["owned"], result["removed_by_harness"])
        self.assertIn(unittest.mock.call("DELETE", "/containers/owned?force=true&v=true"),
                      calls.call_args_list)

    def test_container_sample_crossing_phase_transition_is_discarded(self):
        with patch.dict("os.environ", {"DOCKER_HOST": "unix:///local/socket"}):
            observer = harness.ContainerObserver("trial")
        observer.phase = "baseline"
        observer.containers["owned"] = {}

        def request(method, path):
            self.assertEqual("/containers/owned/stats?stream=false", path)
            observer.phase = "edited"
            observer.stop.set()
            return {"cpu_stats": {"cpu_usage": {"total_usage": 123}},
                    "memory_stats": {"usage": 456, "limit": 512 * 1024**2}}

        with patch.object(observer.api, "containers", return_value=[
                {"Id": "owned", "State": "running"}]), \
                patch.object(observer.api, "request", side_effect=request):
            observer.observe()
        self.assertEqual([], observer.samples)
        self.assertEqual([], observer.errors)

    def test_container_cleanup_does_not_claim_racing_404_removal(self):
        with patch.dict("os.environ", {"DOCKER_HOST": "unix:///local/socket"}):
            observer = harness.ContainerObserver("trial")
        observer.stop.set()
        observer.thread.start()
        row = {"Id": "owned"}
        details = {"Config": {"Labels": {"piston.measurement.trial": "trial"}}}
        with patch.object(observer.api, "containers", side_effect=[[row], []]), \
                patch.object(observer.api, "request", side_effect=[details, None]) as calls:
            result = observer.finish()
        self.assertTrue(result["confirmed"])
        self.assertEqual([], result["removed_by_harness"])
        self.assertIn(unittest.mock.call("DELETE", "/containers/owned?force=true&v=true"),
                      calls.call_args_list)

    def test_container_cleanup_refuses_mismatched_labels_and_survivors(self):
        for mismatch in (True, False):
            with patch.dict("os.environ", {"DOCKER_HOST": "unix:///local/socket"}):
                observer = harness.ContainerObserver("trial")
            observer.stop.set()
            observer.thread.start()
            row = {"Id": "owned", "Labels": {"piston.measurement.trial": "trial"}}
            details = {"Config": {"Labels": {
                "piston.measurement.trial": "other" if mismatch else "trial"}}}
            with patch.object(observer.api, "containers", return_value=[row]), \
                    patch.object(observer.api, "request", return_value=details) as calls:
                with self.assertRaises(harness.ContainerAPIError):
                    observer.finish()
                if mismatch:
                    self.assertFalse(any(call.args[0] == "DELETE"
                                         for call in calls.call_args_list))

    def test_container_telemetry_errors_are_retained(self):
        with patch.dict("os.environ", {"DOCKER_HOST": "unix:///local/socket"}):
            observer = harness.ContainerObserver("trial")

        def fail(trial_id):
            observer.stop.set()
            raise OSError("socket unavailable")

        with patch.object(observer.api, "containers", side_effect=fail):
            observer.observe()
        self.assertEqual("socket unavailable", observer.errors[0]["error"])


if __name__ == "__main__":
    unittest.main()
