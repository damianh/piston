"""Release orchestration tests using temporary Git origins and fake HTTP."""

import io
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import unittest
from datetime import date, datetime, timezone
from unittest.mock import patch
from urllib.error import HTTPError, URLError
import zipfile

import release_publish
from release_publish import ReleaseError
from release_version import ReleaseVersion


REPO = "example/piston"
API = "https://github.invalid"
DAY = date(2026, 11, 1)
WORKFLOW_URL = f"{API}/repos/{REPO}/actions/workflows/release.yml"


def _action_url(run_id):
    return f"{API}/repos/{REPO}/actions/runs/{run_id}"


def _release_url(version):
    return f"{API}/repos/{REPO}/releases/tags/{version.tag}"


def _package_url(version):
    number = version.version.lower()
    return f"https://api.nuget.org/v3-flatcontainer/piston/{number}/piston.{number}.nupkg"


def _package(
    version,
    source_sha,
    *,
    repo=REPO,
    nuspec_version=None,
    mcp_version=None,
    identifier="Piston",
    dll=b"expected executable",
    signature=b"signature",
):
    number = version.version
    nuspec = (
        '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">'
        "<metadata>"
        f"<id>Piston</id><version>{nuspec_version or number}</version>"
        f'<repository type="git" url="https://github.com/{repo}" commit="{source_sha}" />'
        "</metadata></package>"
    )
    server = {
        "version": mcp_version or number,
        "packages": [{"identifier": identifier, "version": mcp_version or number}],
    }
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as archive:
        archive.writestr("Piston.nuspec", nuspec)
        archive.writestr(".mcp/server.json", json.dumps(server))
        archive.writestr("tools/net10.0/any/Piston.dll", dll)
        archive.writestr(".signature.p7s", signature)
    return buffer.getvalue()


class FakeHttp:
    """An explicit route table; unknown HTTP requests never reach the network."""

    def __init__(self):
        self.responses = {}
        self.requests = []

    def json(self, url, value):
        self.responses[url] = json.dumps(value).encode("utf-8")

    def open(self, request, timeout):
        url = request.full_url
        self.requests.append(url)
        if url not in self.responses:
            raise AssertionError(f"Unexpected HTTP request: {url}")
        response = self.responses[url]
        if callable(response):
            response = response()
        if isinstance(response, int):
            raise HTTPError(url, response, "controlled response", hdrs=None, fp=None)
        if isinstance(response, Exception):
            raise response
        return io.BytesIO(response)


class ApiFixture(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="piston-release-tests-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.http = FakeHttp()
        self.http.json(WORKFLOW_URL, {"path": ".github/workflows/release.yml"})
        opener = patch.object(release_publish.urllib.request, "build_opener", return_value=self.http)
        opener.start()
        self.addCleanup(opener.stop)
        environment = patch.dict(os.environ, {"GH_TOKEN": "fixture-token", "GITHUB_API_URL": API})
        environment.start()
        self.addCleanup(environment.stop)

    def success(self, run_id):
        self.http.json(
            _action_url(run_id),
            {
                "id": run_id,
                "path": ".github/workflows/release.yml@refs/heads/main",
                "status": "completed",
                "conclusion": "success",
            },
        )

    def public_package(self, version, source_sha, **kwargs):
        self.http.responses[_package_url(version)] = _package(version, source_sha, **kwargs)

    def public_release(self, version, assets=None, **changes):
        response = {
            "tag_name": version.tag,
            "draft": False,
            "prerelease": False,
            "assets": assets if assets is not None else [
                {"name": f"Piston.{version.version}.nupkg", "state": "uploaded"}
            ],
        }
        response.update(changes)
        self.http.json(_release_url(version), response)

    def local_package(self, version, source_sha, **kwargs):
        path = self.root / f"Piston.{version.version}.nupkg"
        path.write_bytes(_package(version, source_sha, **kwargs))
        return path


class ReleaseWorkflowGateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.workflow = (Path(__file__).resolve().parents[1] / "workflows" / "release.yml").read_text(
            encoding="utf-8"
        )

    def can_run(self, job, results, *, publish=True, cancelled=False):
        job_match = re.search(
            rf"(?ms)^  {re.escape(job)}:\n(.*?)(?=^  [a-z][a-z0-9-]*:\n|\Z)",
            self.workflow,
        )
        self.assertIsNotNone(job_match, f"Missing job: {job}")
        body = job_match.group(1)
        needs_match = re.search(r"(?m)^    needs: \[([^\]]+)\]\s*$", body)
        self.assertIsNotNone(needs_match, f"Missing direct dependencies: {job}")
        direct_dependencies = {name.strip() for name in needs_match.group(1).split(",")}
        guard_match = re.search(r"(?m)^    if: >-\n((?:^      .+\n)+)", body)
        self.assertIsNotNone(guard_match, f"Missing explicit job guard: {job}")
        guard = " ".join(line.strip() for line in guard_match.group(1).splitlines())

        checks = []
        result_checks = set()
        has_cancellation_check = False
        has_publish_check = False
        for clause in guard.split(" && "):
            if clause == "!cancelled()":
                has_cancellation_check = True
                checks.append(not cancelled)
            elif clause == "needs.context.outputs.publish == 'true'":
                has_publish_check = True
                checks.append(publish)
            else:
                match = re.fullmatch(r"needs\.([a-z-]+)\.result == 'success'", clause)
                self.assertIsNotNone(match, f"Unsupported job guard clause: {clause}")
                dependency = match.group(1)
                result_checks.add(dependency)
                checks.append(results[dependency] == "success")

        # A status function removes the implicit success() gate on skipped ancestors.
        self.assertTrue(has_cancellation_check, f"Missing cancellation guard: {job}")
        self.assertEqual(direct_dependencies, result_checks)
        self.assertEqual(job == "publish", has_publish_check)
        return all(checks)

    def test_skipped_stable_checks_do_not_block_dev_publish_or_verification(self):
        results = {
            "context": "success",
            "test": "skipped",
            "distribution": "skipped",
            "package": "success",
            "publish": "success",
        }
        self.assertTrue(self.can_run("publish", results))
        self.assertTrue(self.can_run("verify-public", results))

    def test_stable_success_allows_publish_and_verification(self):
        results = {
            "context": "success",
            "test": "success",
            "distribution": "success",
            "package": "success",
            "publish": "success",
        }
        self.assertTrue(self.can_run("publish", results))
        self.assertTrue(self.can_run("verify-public", results))

    def test_failed_cancelled_or_skipped_direct_dependencies_block_jobs(self):
        baseline = {"context": "success", "package": "success", "publish": "success"}
        for job, dependencies in (
            ("publish", ("context", "package")),
            ("verify-public", ("context", "package", "publish")),
        ):
            for dependency in dependencies:
                for result in ("failure", "cancelled", "skipped"):
                    with self.subTest(job=job, dependency=dependency, result=result):
                        results = dict(baseline, **{dependency: result})
                        self.assertFalse(self.can_run(job, results))

    def test_workflow_cancellation_blocks_publish_and_verification(self):
        results = {"context": "success", "package": "success", "publish": "success"}
        for job in ("publish", "verify-public"):
            with self.subTest(job=job):
                self.assertFalse(self.can_run(job, results, cancelled=True))

    def test_validation_only_skips_publish_and_verification(self):
        results = {"context": "success", "package": "success", "publish": "skipped"}
        self.assertFalse(self.can_run("publish", results, publish=False))
        self.assertFalse(self.can_run("verify-public", results, publish=False))


class ReleaseContextTests(unittest.TestCase):
    tested_sha = "a" * 40
    workflow_sha = "b" * 40

    def event(self, **changes):
        run = {
            "conclusion": "success",
            "event": "push",
            "head_branch": "main",
            "head_repository": {"full_name": REPO},
            "head_sha": self.tested_sha,
        }
        run.update(changes)
        return {"workflow_run": run}

    def test_select_context_uses_successful_main_push_ci_sha_even_when_stale(self):
        selected = release_publish.select_context(
            "workflow_run", "refs/heads/main", REPO, self.workflow_sha, self.event(), False
        )
        self.assertEqual(self.tested_sha, selected.source_sha)
        self.assertEqual("dev", selected.kind)
        self.assertIs(selected.publish, True)

    def test_select_context_rejects_nonqualifying_ci_events(self):
        cases = (
            ("failed", {"conclusion": "failure"}),
            ("cancelled", {"conclusion": "cancelled"}),
            ("incomplete", {"conclusion": None}),
            ("pull_request", {"event": "pull_request"}),
            ("fork", {"head_repository": {"full_name": "fork/piston"}}),
            ("missing_repository", {"head_repository": None}),
            ("non_main_branch", {"head_branch": "feature"}),
        )
        for label, changes in cases:
            with self.subTest(case=label):
                with self.assertRaises(ReleaseError):
                    release_publish.select_context(
                        "workflow_run", "refs/heads/main", REPO, self.workflow_sha, self.event(**changes), False
                    )

    def test_select_context_manual_main_selects_stable_publish_or_validation(self):
        for label, publish in (("publish", True), ("validation_only", False)):
            with self.subTest(case=label):
                selected = release_publish.select_context(
                    "workflow_dispatch", "refs/heads/main", REPO, self.workflow_sha.upper(), {}, publish
                )
                self.assertEqual(self.workflow_sha, selected.source_sha)
                self.assertEqual("stable", selected.kind)
                self.assertIs(selected.publish, publish)

    def test_select_context_rejects_manual_non_main(self):
        for label, ref in (("branch", "refs/heads/feature"), ("tag", "refs/tags/v2026.1101.1")):
            with self.subTest(case=label):
                with self.assertRaises(ReleaseError):
                    release_publish.select_context("workflow_dispatch", ref, REPO, self.workflow_sha, {}, True)

    def test_select_context_rejects_invalid_source_sha(self):
        for label, sha in (("short", "abc123"), ("non_hex", "g" * 40), ("missing", None)):
            with self.subTest(case=label):
                with self.assertRaises(ReleaseError):
                    release_publish.select_context(
                        "workflow_run", "refs/heads/main", REPO, self.workflow_sha, self.event(head_sha=sha), False
                    )
        with self.assertRaises(ReleaseError):
            release_publish.select_context("workflow_dispatch", "refs/heads/main", REPO, "abc123", {}, False)

    def test_select_context_rejects_missing_or_unsupported_events(self):
        for label, name, event in (
            ("missing_run", "workflow_run", {}),
            ("invalid_run", "workflow_run", {"workflow_run": []}),
            ("unsupported_event", "push", self.event()),
        ):
            with self.subTest(case=label):
                with self.assertRaises(ReleaseError):
                    release_publish.select_context(name, "refs/heads/main", REPO, self.workflow_sha, event, False)

    def test_context_cli_exports_the_tested_sha_and_dev_publication(self):
        with tempfile.TemporaryDirectory(prefix="piston-release-context-") as directory:
            event_path = Path(directory) / "event.json"
            output_path = Path(directory) / "output"
            event_path.write_text(json.dumps(self.event()), encoding="utf-8")
            arguments = [
                "release_publish.py", "context", "--event-name", "workflow_run",
                "--ref", "refs/heads/main", "--workflow-sha", self.workflow_sha,
                "--event-file", str(event_path), "--repo", REPO, "--manual-publish", "false",
            ]
            with patch.object(release_publish.sys, "argv", arguments):
                with patch.dict(os.environ, {"GITHUB_OUTPUT": str(output_path)}):
                    release_publish.main()
            self.assertEqual(
                f"source_sha={self.tested_sha}\nkind=dev\npublish=true\n",
                output_path.read_text(encoding="utf-8"),
            )


class GitFixture(ApiFixture):
    def setUp(self):
        super().setUp()
        self.origin = self.root / "origin.git"
        self.worktree = self.root / "worktree"
        self.worktree.mkdir()
        self.command("git", "init", "--bare", "--initial-branch=main", str(self.origin))
        self.git("init", "--initial-branch=main")
        self.source_sha = self.commit("First tested commit")
        self.git("remote", "add", "origin", str(self.origin))
        self.git("push", "--quiet", "--set-upstream", "origin", "main")

    def command(self, *arguments, cwd=None):
        result = subprocess.run(arguments, cwd=cwd, text=True, capture_output=True, check=True)
        return result.stdout.strip()

    def git(self, *arguments):
        return self.command("git", *arguments, cwd=self.worktree)

    def commit(self, message):
        self.git(
            "-c", "user.name=Release Test", "-c", "user.email=release@example.invalid",
            "commit", "--allow-empty", "-m", message,
        )
        return self.git("rev-parse", "HEAD")

    def origin_tags(self):
        return self.git("--git-dir", str(self.origin), "tag", "--list").splitlines()

    def tag(self, version, run_id, *, source_sha=None, body=None, annotated=True):
        source_sha = source_sha or self.source_sha
        if body is None:
            body = json.dumps({
                "piston_release": 1,
                "run_id": run_id,
                "source_sha": source_sha,
                "version": version.version,
            })
        if annotated:
            self.git(
                "-c", "user.name=Release Test", "-c", "user.email=release@example.invalid",
                "tag", "-a", version.tag, source_sha, "-m", body,
            )
        else:
            self.git("tag", version.tag, source_sha)
        self.git("push", "--quiet", "origin", f"refs/tags/{version.tag}")

    def prepare_at(self, day, *, source_sha=None, run_id=100, dev=True, preview=False):
        class FixedUtcClock:
            @staticmethod
            def now(tz):
                if tz is not timezone.utc:
                    raise AssertionError("Reservation must use the UTC clock.")
                return datetime(day.year, day.month, day.day, 23, 59, tzinfo=timezone.utc)

        with patch.object(release_publish, "datetime", FixedUtcClock):
            return release_publish.prepare(
                self.worktree, REPO, source_sha or self.source_sha, run_id, dev, preview
            )


class ReleaseReservationTests(GitFixture):
    def test_prepare_pushes_annotated_reservation_to_local_origin(self):
        version, reused = self.prepare_at(DAY, run_id=41)
        self.assertEqual("2026.1101.1-dev", version.version)
        self.assertEqual("v2026.1101.1-dev", version.tag)
        self.assertIs(reused, False)
        self.assertEqual(["v2026.1101.1-dev"], self.origin_tags())
        self.assertEqual("tag", self.git("--git-dir", str(self.origin), "cat-file", "-t", f"refs/tags/{version.tag}"))
        raw = self.git("--git-dir", str(self.origin), "cat-file", "-p", f"refs/tags/{version.tag}")
        headers, body = raw.split("\n\n", 1)
        self.assertIn(f"object {self.source_sha}", headers.splitlines())
        self.assertIn("type commit", headers.splitlines())
        self.assertEqual(
            {
                "piston_release": 1,
                "run_id": 41,
                "source_sha": self.source_sha,
                "version": "2026.1101.1-dev",
                "verified_through": None,
            },
            json.loads(body),
        )

    def test_prepare_retry_reuses_reservation_after_utc_rollover(self):
        first, reused = self.prepare_at(DAY, run_id=41)
        self.assertIs(reused, False)
        original_object = self.git("rev-parse", f"refs/tags/{first.tag}")
        second, reused = self.prepare_at(date(2026, 11, 2), run_id=41)
        self.assertIs(reused, True)
        self.assertEqual("2026.1101.1-dev", second.version)
        self.assertEqual(original_object, self.git("rev-parse", f"refs/tags/{second.tag}"))
        self.assertEqual(["v2026.1101.1-dev"], self.origin_tags())

    def test_prepare_uses_utc_date_after_prior_release_verification(self):
        prior = ReleaseVersion(DAY, 1, True)
        self.tag(prior, 41)
        self.success(41)

        class RollingUtcClock:
            current = datetime(2026, 11, 1, 23, 59, tzinfo=timezone.utc)

            @classmethod
            def now(cls, tz):
                if tz is not timezone.utc:
                    raise AssertionError("Reservation must use the UTC clock.")
                return cls.current

        def verify_after_midnight():
            RollingUtcClock.current = datetime(2026, 11, 2, 0, 0, tzinfo=timezone.utc)
            return _package(prior, self.source_sha)

        self.http.responses[_package_url(prior)] = verify_after_midnight
        with patch.object(release_publish, "datetime", RollingUtcClock):
            version, reused = release_publish.prepare(self.worktree, REPO, self.source_sha, 42, False, False)
        self.assertIs(reused, False)
        self.assertEqual("2026.1102.1", version.version)
        self.assertEqual(["v2026.1101.1-dev", "v2026.1102.1"], self.origin_tags())

    def test_prepare_reserves_earlier_commit_after_main_advances(self):
        tested_sha = self.source_sha
        latest_sha = self.commit("Newer main commit")
        self.git("push", "--quiet", "origin", "main")
        version, reused = self.prepare_at(DAY, source_sha=tested_sha, run_id=42)
        self.assertIs(reused, False)
        self.assertEqual(latest_sha, self.git("--git-dir", str(self.origin), "rev-parse", "refs/heads/main"))
        self.assertEqual(tested_sha, self.git("--git-dir", str(self.origin), "rev-parse", f"{version.tag}^{{}}"))

    def test_prepare_rejects_retry_with_changed_inputs(self):
        self.prepare_at(DAY, run_id=41)
        new_sha = self.commit("Different source")
        for label, source_sha, dev in (
            ("changed_source", new_sha, True),
            ("changed_kind", self.source_sha, False),
        ):
            with self.subTest(case=label):
                with self.assertRaises(ReleaseError):
                    self.prepare_at(date(2026, 11, 2), source_sha=source_sha, run_id=41, dev=dev)
        self.assertEqual(["v2026.1101.1-dev"], self.origin_tags())

    def test_prepare_rejects_out_of_order_retry(self):
        self.tag(ReleaseVersion(DAY, 1, True), 41)
        self.tag(ReleaseVersion(DAY, 2, False), 42)
        with self.assertRaises(ReleaseError):
            self.prepare_at(DAY, run_id=41)
        self.assertEqual(["v2026.1101.1-dev", "v2026.1101.2"], self.origin_tags())

    def test_prepare_preview_does_not_create_a_tag(self):
        version, reused = self.prepare_at(DAY, run_id=41, dev=False, preview=True)
        self.assertEqual("2026.1101.1", version.version)
        self.assertIs(reused, False)
        self.assertEqual([], self.origin_tags())

    def test_prepare_blocks_incomplete_or_failed_previous_run(self):
        prior = ReleaseVersion(DAY, 1, True)
        self.tag(prior, 41)
        cases = (
            ("in_progress", {"status": "in_progress", "conclusion": None}),
            ("failed", {"status": "completed", "conclusion": "failure"}),
            ("cancelled", {"status": "completed", "conclusion": "cancelled"}),
            ("wrong_workflow", {"path": ".github/workflows/ci.yml"}),
            ("wrong_run_id", {"id": 99}),
        )
        for label, changes in cases:
            with self.subTest(case=label):
                run = {"id": 41, "path": ".github/workflows/release.yml", "status": "completed", "conclusion": "success"}
                run.update(changes)
                self.http.json(_action_url(41), run)
                with self.assertRaises(ReleaseError):
                    self.prepare_at(DAY, run_id=42)
                self.assertEqual(["v2026.1101.1-dev"], self.origin_tags())

    def test_prepare_blocks_failed_previous_run_after_utc_rollover(self):
        prior = ReleaseVersion(DAY, 1, True)
        self.tag(prior, 41)
        self.http.json(
            _action_url(41),
            {"id": 41, "path": ".github/workflows/release.yml", "status": "completed", "conclusion": "failure"},
        )
        with self.assertRaises(ReleaseError):
            self.prepare_at(date(2026, 11, 2), run_id=42)
        self.assertEqual(["v2026.1101.1-dev"], self.origin_tags())

    def test_prepare_fails_closed_when_github_status_is_unavailable(self):
        prior = ReleaseVersion(DAY, 1, True)
        self.tag(prior, 41)
        for label, response in (("server_error", 503), ("transport_error", URLError("controlled outage"))):
            with self.subTest(case=label):
                self.http.responses[_action_url(41)] = response
                with self.assertRaises(ReleaseError):
                    self.prepare_at(DAY, run_id=42)
                self.assertEqual(["v2026.1101.1-dev"], self.origin_tags())

    def test_prepare_rejects_clock_before_latest_reservation(self):
        prior = ReleaseVersion(date(2026, 11, 2), 1, True)
        self.tag(prior, 41)
        with self.assertRaises(ReleaseError):
            self.prepare_at(DAY, run_id=42)
        self.assertEqual(["v2026.1102.1-dev"], self.origin_tags())

    def test_prepare_accepts_verified_successful_previous_run(self):
        prior = ReleaseVersion(DAY, 1, True)
        self.tag(prior, 41)
        self.success(41)
        self.public_package(prior, self.source_sha)
        version, reused = self.prepare_at(DAY, run_id=42, dev=False)
        self.assertIs(reused, False)
        self.assertEqual("2026.1101.2", version.version)
        self.assertEqual(["v2026.1101.1-dev", "v2026.1101.2"], self.origin_tags())

    def test_prepare_shares_stable_and_dev_counters(self):
        prior = ReleaseVersion(DAY, 4, False)
        self.tag(prior, 41)
        self.success(41)
        self.public_package(prior, self.source_sha)
        self.public_release(prior)
        with patch.object(release_publish, "download_github_asset", return_value=_package(prior, self.source_sha)):
            version, reused = self.prepare_at(DAY, run_id=42, dev=True)
        self.assertIs(reused, False)
        self.assertEqual("2026.1101.5-dev", version.version)
        self.assertEqual(["v2026.1101.4", "v2026.1101.5-dev"], self.origin_tags())

    def test_prepare_accepts_verified_expired_stable_run(self):
        prior = ReleaseVersion(DAY, 2, False)
        self.tag(prior, 41)
        self.http.responses[_action_url(41)] = 404
        self.public_package(prior, self.source_sha)
        self.public_release(prior)
        with patch.object(release_publish, "download_github_asset", return_value=_package(prior, self.source_sha)):
            version, reused = self.prepare_at(date(2026, 11, 2), run_id=42)
        self.assertIs(reused, False)
        self.assertEqual("2026.1102.1-dev", version.version)
        self.assertEqual(["v2026.1101.2", "v2026.1102.1-dev"], self.origin_tags())

    def test_prepare_blocks_expired_run_without_complete_public_evidence(self):
        prior = ReleaseVersion(DAY, 1, False)
        self.tag(prior, 41)
        self.http.responses[_action_url(41)] = 404
        for case in ("missing_package", "missing_release", "missing_asset"):
            with self.subTest(case=case):
                self.public_package(prior, self.source_sha)
                self.public_release(prior)
                if case == "missing_package":
                    self.http.responses[_package_url(prior)] = 404
                elif case == "missing_release":
                    self.http.responses[_release_url(prior)] = 404
                else:
                    self.public_release(prior, assets=[])
                with self.assertRaises(ReleaseError):
                    self.prepare_at(date(2026, 11, 2), run_id=42)
                self.assertEqual(["v2026.1101.1"], self.origin_tags())

    def test_prepare_blocks_expired_run_when_workflow_api_is_inaccessible(self):
        prior = ReleaseVersion(DAY, 1, True)
        self.tag(prior, 41)
        self.http.responses[_action_url(41)] = 404
        self.http.responses[WORKFLOW_URL] = 503
        self.public_package(prior, self.source_sha)
        with self.assertRaises(ReleaseError):
            self.prepare_at(date(2026, 11, 2), run_id=42)
        self.assertEqual(["v2026.1101.1-dev"], self.origin_tags())
        self.assertNotIn(_package_url(prior), self.http.requests)

    def test_prepare_blocks_expired_stable_run_with_mismatched_release_asset(self):
        prior = ReleaseVersion(DAY, 1, False)
        self.tag(prior, 41)
        self.http.responses[_action_url(41)] = 404
        self.public_package(prior, self.source_sha)
        self.public_release(prior)
        wrong_asset = _package(prior, self.source_sha, dll=b"different executable")
        with patch.object(release_publish, "download_github_asset", return_value=wrong_asset):
            with self.assertRaises(ReleaseError):
                self.prepare_at(date(2026, 11, 2), run_id=42)
        self.assertEqual(["v2026.1101.1"], self.origin_tags())

    def test_prepare_blocks_missing_or_mismatched_public_evidence(self):
        prior = ReleaseVersion(DAY, 1, False)
        self.tag(prior, 41)
        self.success(41)
        cases = ("missing_package", "wrong_commit", "missing_release", "missing_asset", "nuget_error")
        for case in cases:
            with self.subTest(case=case):
                self.public_package(prior, self.source_sha)
                self.public_release(prior)
                if case == "missing_package":
                    self.http.responses[_package_url(prior)] = 404
                elif case == "wrong_commit":
                    self.public_package(prior, "f" * 40)
                elif case == "missing_release":
                    self.http.responses[_release_url(prior)] = 404
                elif case == "missing_asset":
                    self.public_release(prior, assets=[])
                else:
                    self.http.responses[_package_url(prior)] = 503
                with self.assertRaises(ReleaseError):
                    self.prepare_at(DAY, run_id=42)
                self.assertEqual(["v2026.1101.1"], self.origin_tags())

    def test_prepare_blocks_unresolved_older_reservation(self):
        older = ReleaseVersion(DAY, 1, True)
        newer = ReleaseVersion(DAY, 2, True)
        self.tag(older, 40)
        self.tag(newer, 41)
        self.http.json(
            _action_url(40),
            {"id": 40, "path": ".github/workflows/release.yml", "status": "completed", "conclusion": "failure"},
        )
        self.success(41)
        self.public_package(newer, self.source_sha)
        with self.assertRaises(ReleaseError):
            self.prepare_at(DAY, run_id=42)
        self.assertEqual(["v2026.1101.1-dev", "v2026.1101.2-dev"], self.origin_tags())

    def test_prepare_retry_blocks_unresolved_older_reservation(self):
        older = ReleaseVersion(DAY, 1, True)
        own = ReleaseVersion(DAY, 2, True)
        self.tag(older, 40)
        self.tag(own, 41)
        self.http.json(
            _action_url(40),
            {"id": 40, "path": ".github/workflows/release.yml", "status": "completed", "conclusion": "failure"},
        )
        with self.assertRaises(ReleaseError):
            self.prepare_at(date(2026, 11, 2), run_id=41)
        self.assertEqual(["v2026.1101.1-dev", "v2026.1101.2-dev"], self.origin_tags())

    def test_verified_history_checkpoint_avoids_rechecking_older_publications(self):
        first, _ = self.prepare_at(DAY, run_id=40)
        self.success(40)
        self.public_package(first, self.source_sha)
        second, _ = self.prepare_at(DAY, run_id=41, dev=False)
        raw = self.git("cat-file", "-p", f"refs/tags/{second.tag}")
        self.assertEqual(first.tag, json.loads(raw.split("\n\n", 1)[1])["verified_through"])

        self.success(41)
        self.public_package(second, self.source_sha)
        self.public_release(second)
        self.http.responses.pop(_action_url(40))
        self.http.responses.pop(_package_url(first))
        self.http.requests.clear()
        with patch.object(release_publish, "download_github_asset", return_value=_package(second, self.source_sha)):
            third, reused = self.prepare_at(DAY, run_id=42)
        self.assertIs(reused, False)
        self.assertEqual("2026.1101.3-dev", third.version)
        self.assertNotIn(_action_url(40), self.http.requests)
        self.assertNotIn(_package_url(first), self.http.requests)

    def test_retry_reuses_a_checkpoint_after_earlier_publication_was_verified(self):
        first, _ = self.prepare_at(DAY, run_id=40)
        self.success(40)
        self.public_package(first, self.source_sha)
        second, _ = self.prepare_at(DAY, run_id=41)
        self.http.requests.clear()
        retry, reused = self.prepare_at(date(2026, 11, 2), run_id=41)
        self.assertIs(reused, True)
        self.assertEqual(second, retry)
        self.assertEqual([], self.http.requests)

    def test_prepare_rejects_a_broken_verified_history_checkpoint(self):
        self.tag(ReleaseVersion(DAY, 1, True), 40)
        version = ReleaseVersion(DAY, 2, False)
        body = json.dumps({
            "piston_release": 1,
            "run_id": 41,
            "source_sha": self.source_sha,
            "version": version.version,
            "verified_through": "v2026.1031.1",
        })
        self.tag(version, 41, body=body)
        with self.assertRaises(ReleaseError):
            self.prepare_at(DAY, run_id=42)
        self.assertEqual(["v2026.1101.1-dev", "v2026.1101.2"], self.origin_tags())

    def test_prepare_rejects_duplicate_counter(self):
        self.tag(ReleaseVersion(DAY, 1, False), 40)
        self.tag(ReleaseVersion(DAY, 1, True), 41)
        with self.assertRaises(ReleaseError):
            self.prepare_at(DAY, run_id=42)
        self.assertEqual(["v2026.1101.1", "v2026.1101.1-dev"], self.origin_tags())

    def test_prepare_rejects_duplicate_run_reservations(self):
        self.tag(ReleaseVersion(DAY, 1, True), 40)
        self.tag(ReleaseVersion(DAY, 2, True), 40)
        with self.assertRaises(ReleaseError):
            self.prepare_at(DAY, run_id=42)
        self.assertEqual(["v2026.1101.1-dev", "v2026.1101.2-dev"], self.origin_tags())

    def test_prepare_rejects_lightweight_calendar_tag(self):
        self.tag(ReleaseVersion(DAY, 1, True), 40, annotated=False)
        with self.assertRaises(ReleaseError):
            self.prepare_at(DAY, run_id=42)
        self.assertEqual(["v2026.1101.1-dev"], self.origin_tags())

    def test_read_reservation_rejects_invalid_metadata(self):
        cases = (
            ("invalid_json", "not-json"),
            ("wrong_marker", json.dumps({"piston_release": 0, "run_id": 40, "source_sha": self.source_sha, "version": "2026.1101.2-dev"})),
            ("wrong_version", json.dumps({"piston_release": 1, "run_id": 40, "source_sha": self.source_sha, "version": "2026.1101.1-dev"})),
            ("wrong_source", json.dumps({"piston_release": 1, "run_id": 40, "source_sha": "f" * 40, "version": "2026.1101.4-dev"})),
            ("invalid_run", json.dumps({"piston_release": 1, "run_id": "40", "source_sha": self.source_sha, "version": "2026.1101.5-dev"})),
        )
        for counter, (label, body) in enumerate(cases, start=1):
            with self.subTest(case=label):
                version = ReleaseVersion(DAY, counter, True)
                self.tag(version, 40, body=body)
                with self.assertRaises(ReleaseError):
                    release_publish.read_reservation(self.worktree, version.tag, version)

    def test_prepare_rejects_remote_tag_conflict_without_overwriting(self):
        actual_git = release_publish.git

        def inject_competing_tag(source_dir, *arguments):
            result = actual_git(source_dir, *arguments)
            if "-a" in arguments and "tag" in arguments:
                tag = arguments[arguments.index("-a") + 1]
                self.git(
                    "--git-dir", str(self.origin),
                    "-c", "user.name=Other Publisher", "-c", "user.email=other@example.invalid",
                    "tag", "-a", tag, self.source_sha, "-m", "competing reservation",
                )
            return result

        with patch.object(release_publish, "git", side_effect=inject_competing_tag):
            with self.assertRaises(ReleaseError):
                self.prepare_at(DAY, run_id=41)
        self.assertEqual(["v2026.1101.1-dev"], self.origin_tags())
        raw = self.git("--git-dir", str(self.origin), "cat-file", "-p", "refs/tags/v2026.1101.1-dev")
        self.assertTrue(raw.endswith("competing reservation"))


class NuGetReconciliationTests(ApiFixture):
    def setUp(self):
        super().setUp()
        self.version = ReleaseVersion(DAY, 2, True)
        self.source_sha = "c" * 40
        self.package_path = self.local_package(self.version, self.source_sha)

    def test_reconcile_requires_publish_when_package_missing(self):
        self.http.responses[_package_url(self.version)] = 404
        for label, retry in (("first_attempt", False), ("retry", True)):
            with self.subTest(case=label):
                self.assertIs(
                    release_publish.reconcile(self.package_path, self.version, self.source_sha, REPO, retry),
                    True,
                )

    def test_retry_eligibility_requires_a_prior_reservation_or_prior_attempt_artifact(self):
        cases = (
            ("first_attempt", False, "piston-nuget-tool-41-1", 1, False),
            ("new_reservation_on_later_attempt", False, "piston-nuget-tool-41-2", 2, False),
            ("reused_reservation", True, "piston-nuget-tool-41-2", 2, True),
            ("earlier_package_job", False, "piston-nuget-tool-41-1", 2, True),
        )
        for label, reused, artifact, attempt, expected in cases:
            with self.subTest(case=label):
                self.assertIs(expected, release_publish.is_reserved_retry(reused, artifact, 41, attempt))

    def test_retry_eligibility_rejects_unrelated_or_future_artifacts(self):
        cases = (
            ("different_run", "piston-nuget-tool-42-1", 41, 2),
            ("future_attempt", "piston-nuget-tool-41-3", 41, 2),
            ("zero_attempt", "piston-nuget-tool-41-0", 41, 2),
            ("invalid_run", "piston-nuget-tool-0-1", 0, 1),
            ("invalid_current_attempt", "piston-nuget-tool-41-1", 41, 0),
        )
        for label, artifact, run_id, attempt in cases:
            with self.subTest(case=label):
                with self.assertRaises(ReleaseError):
                    release_publish.is_reserved_retry(False, artifact, run_id, attempt)

    def test_reconcile_cli_rejects_duplicate_for_first_reservation_on_a_later_attempt(self):
        self.public_package(self.version, self.source_sha)
        arguments = [
            "release_publish.py", "reconcile", "--package", str(self.package_path),
            "--version", self.version.version, "--source-sha", self.source_sha, "--repo", REPO,
            "--reused", "false", "--artifact", "piston-nuget-tool-41-2",
            "--run-id", "41", "--run-attempt", "2",
        ]
        with patch.object(release_publish.sys, "argv", arguments):
            with self.assertRaises(ReleaseError):
                release_publish.main()

    def test_reconcile_cli_recovers_an_artifact_from_an_earlier_attempt(self):
        self.public_package(self.version, self.source_sha)
        arguments = [
            "release_publish.py", "reconcile", "--package", str(self.package_path),
            "--version", self.version.version, "--source-sha", self.source_sha, "--repo", REPO,
            "--reused", "false", "--artifact", "piston-nuget-tool-41-1",
            "--run-id", "41", "--run-attempt", "2",
        ]
        with patch.object(release_publish.sys, "argv", arguments):
            with patch.object(release_publish, "write_outputs") as write_outputs:
                release_publish.main()
        write_outputs.assert_called_once_with(push="false")

    def test_reconcile_rejects_unknown_duplicate(self):
        self.public_package(self.version, self.source_sha)
        with self.assertRaises(ReleaseError):
            release_publish.reconcile(self.package_path, self.version, self.source_sha, REPO, False)

    def test_reconcile_recovers_identical_reserved_package(self):
        self.public_package(self.version, self.source_sha, signature=b"nuget-side signature")
        should_push = release_publish.reconcile(self.package_path, self.version, self.source_sha, REPO, True)
        self.assertIs(should_push, False)

    def test_reconcile_rejects_mismatched_published_identity(self):
        cases = (
            ("wrong_commit", {"source_sha": "f" * 40}),
            ("wrong_repository", {"repo": "other/piston"}),
            ("wrong_nuspec_version", {"nuspec_version": "2026.1101.3-dev"}),
            ("wrong_mcp_version", {"mcp_version": "2026.1101.3-dev"}),
            ("wrong_mcp_identifier", {"identifier": "Other"}),
        )
        for label, changes in cases:
            with self.subTest(case=label):
                kwargs = dict(changes)
                source_sha = kwargs.pop("source_sha", self.source_sha)
                self.public_package(self.version, source_sha, **kwargs)
                with self.assertRaises(ReleaseError):
                    release_publish.reconcile(self.package_path, self.version, self.source_sha, REPO, True)

    def test_reconcile_rejects_same_identity_with_different_package_content(self):
        self.public_package(self.version, self.source_sha, dll=b"different executable")
        with self.assertRaises(ReleaseError):
            release_publish.reconcile(self.package_path, self.version, self.source_sha, REPO, True)

    def test_reconcile_fails_closed_on_http_errors(self):
        cases = (
            ("server_error", 503),
            ("forbidden", 403),
            ("transport_error", URLError("controlled outage")),
        )
        for label, response in cases:
            with self.subTest(case=label):
                self.http.responses[_package_url(self.version)] = response
                with self.assertRaises(ReleaseError):
                    release_publish.reconcile(self.package_path, self.version, self.source_sha, REPO, True)

    def test_reconcile_rejects_invalid_local_package_before_network(self):
        self.package_path.write_bytes(b"not a NuGet package")
        with self.assertRaises(ReleaseError):
            release_publish.reconcile(self.package_path, self.version, self.source_sha, REPO, True)
        self.assertEqual([], self.http.requests)

    def test_verify_public_package_accepts_only_matching_release_content(self):
        self.public_package(self.version, self.source_sha, signature=b"nuget-side signature")
        release_publish.verify_public_package(self.package_path, self.version, self.source_sha, REPO)
        self.assertEqual([_package_url(self.version)], self.http.requests)

    def test_verify_public_package_cli_checks_the_downloaded_artifact(self):
        self.public_package(self.version, self.source_sha)
        arguments = [
            "release_publish.py", "verify-package", "--package", str(self.package_path),
            "--version", self.version.version, "--source-sha", self.source_sha, "--repo", REPO,
        ]
        with patch.object(release_publish.sys, "argv", arguments):
            release_publish.main()
        self.assertEqual([_package_url(self.version)], self.http.requests)

    def test_verify_public_package_rejects_missing_or_mismatched_release_content(self):
        cases = (
            ("missing", 404),
            ("wrong_source", _package(self.version, "f" * 40)),
            ("wrong_content", _package(self.version, self.source_sha, dll=b"different executable")),
        )
        for label, response in cases:
            with self.subTest(case=label):
                self.http.responses[_package_url(self.version)] = response
                with self.assertRaises(ReleaseError):
                    release_publish.verify_public_package(
                        self.package_path, self.version, self.source_sha, REPO
                    )


class GitHubReleaseTests(ApiFixture):
    def setUp(self):
        super().setUp()
        self.version = ReleaseVersion(DAY, 3, False)
        self.source_sha = "d" * 40
        self.package_path = self.local_package(self.version, self.source_sha)
        self.release = None
        self.assets = {}
        self.commands = []
        self.http.responses[_release_url(self.version)] = self.release_response
        cli = patch.object(release_publish, "run_command", side_effect=self.run_gh)
        cli.start()
        self.addCleanup(cli.stop)

    def release_response(self):
        if self.release is None:
            return 404
        return json.dumps(self.release).encode("utf-8")

    def run_gh(self, arguments, cwd=None):
        if arguments[:2] != ["gh", "release"]:
            raise AssertionError(f"Unexpected command: {arguments}")
        self.commands.append(arguments)
        action = arguments[2]
        if action == "create":
            self.release = {
                "tag_name": arguments[3],
                "draft": False,
                "prerelease": False,
                "assets": [],
            }
        elif action == "upload":
            path = Path(arguments[4])
            self.assets[path.name] = path.read_bytes()
            self.release["assets"].append({"name": path.name, "state": "uploaded"})
        elif action == "download":
            name = arguments[arguments.index("--pattern") + 1]
            directory = Path(arguments[arguments.index("--dir") + 1])
            (directory / name).write_bytes(self.assets[name])
        else:
            raise AssertionError(f"Unexpected gh release action: {action}")
        return ""

    def existing_release(self, *, assets=None, **changes):
        self.release = {
            "tag_name": self.version.tag,
            "draft": False,
            "prerelease": False,
            "assets": assets if assets is not None else [],
        }
        self.release.update(changes)

    def ensure(self, version=None, package_path=None):
        release_publish.ensure_github_release(
            package_path or self.package_path, version or self.version, self.source_sha, REPO
        )

    def test_ensure_stable_release_create_upload_and_retry_are_idempotent(self):
        self.ensure()
        self.assertEqual(self.version.tag, self.release["tag_name"])
        self.assertEqual(
            [{"name": self.package_path.name, "state": "uploaded"}],
            self.release["assets"],
        )
        self.assertEqual(self.package_path.read_bytes(), self.assets[self.package_path.name])

        self.ensure()
        self.assertEqual(["create", "upload", "download"], [command[2] for command in self.commands])
        self.assertEqual(self.package_path.read_bytes(), self.assets[self.package_path.name])
        self.assertEqual(1, len(self.release["assets"]))

    def test_ensure_stable_release_rejects_mismatched_existing_asset(self):
        name = self.package_path.name
        self.existing_release(assets=[{"name": name, "state": "uploaded"}])
        original = _package(self.version, self.source_sha, dll=b"different executable")
        self.assets[name] = original
        with self.assertRaises(ReleaseError):
            self.ensure()
        self.assertEqual(original, self.assets[name])
        self.assertEqual(["download"], [command[2] for command in self.commands])

    def test_ensure_stable_release_rejects_duplicate_assets(self):
        name = self.package_path.name
        self.existing_release(assets=[{"name": name, "state": "uploaded"}, {"name": name, "state": "uploaded"}])
        with self.assertRaises(ReleaseError):
            self.ensure()
        self.assertEqual([], self.commands)

    def test_ensure_stable_release_rejects_incomplete_asset(self):
        self.existing_release(assets=[{"name": self.package_path.name, "state": "starter"}])
        with self.assertRaises(ReleaseError):
            self.ensure()
        self.assertEqual([], self.commands)

    def test_ensure_stable_release_rejects_invalid_release_state(self):
        cases = (
            ("draft", {"draft": True}),
            ("prerelease", {"prerelease": True}),
            ("wrong_tag", {"tag_name": "v2026.1101.99"}),
        )
        for label, changes in cases:
            with self.subTest(case=label):
                self.existing_release(**changes)
                with self.assertRaises(ReleaseError):
                    self.ensure()
                self.assertEqual([], self.commands)

    def test_ensure_dev_package_never_creates_github_release(self):
        dev = ReleaseVersion(DAY, 4, True)
        path = self.local_package(dev, self.source_sha)
        with self.assertRaises(ReleaseError):
            self.ensure(version=dev, package_path=path)
        self.assertEqual([], self.commands)
        self.assertEqual([], self.http.requests)
