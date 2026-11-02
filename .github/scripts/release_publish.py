"""Reserve and verify immutable Piston releases for GitHub Actions."""

import argparse
from dataclasses import dataclass
from datetime import datetime, timezone
import io
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

from release_version import ReleaseVersion, allocate_version, parse_tag


PACKAGE_ID = "DamianH.Piston"


class ReleaseError(RuntimeError):
    pass


@dataclass(frozen=True)
class RetiredReservation:
    run_id: int
    source_sha: str
    package_id: str
    reason: str


RETIRED_RESERVATIONS = {
    "v2026.1101.1-dev": RetiredReservation(
        run_id=41339003060,
        source_sha="ce5c63d4da00690f40141435e59db7e8122b972f",
        package_id="Piston",
        reason="NuGet trusted-publisher login failed before publication.",
    ),
}


@dataclass(frozen=True)
class Reservation:
    version: ReleaseVersion
    run_id: int
    source_sha: str
    has_verified_history: bool
    verified_through: str | None


@dataclass(frozen=True)
class ReleaseContext:
    source_sha: str
    kind: str
    publish: bool


def select_context(
    event_name: str,
    ref: str,
    repo: str,
    workflow_sha: str,
    event: dict,
    manual_publish: bool,
) -> ReleaseContext:
    repo_path(repo)
    if event_name == "workflow_dispatch":
        if ref != "refs/heads/main":
            raise ReleaseError("Stable releases and validation must be dispatched from main.")
        return ReleaseContext(checked_sha(workflow_sha), "stable", manual_publish)
    if event_name == "workflow_run":
        run = event.get("workflow_run")
        if not isinstance(run, dict):
            raise ReleaseError("The completed CI workflow is missing from the event.")
        head_repository = run.get("head_repository")
        if (
            run.get("conclusion") != "success"
            or run.get("event") != "push"
            or run.get("head_branch") != "main"
            or not isinstance(head_repository, dict)
            or head_repository.get("full_name") != repo
        ):
            raise ReleaseError("Only successful main-branch push CI runs can publish dev packages.")
        source_sha = run.get("head_sha")
        if not isinstance(source_sha, str):
            raise ReleaseError("The completed CI workflow has no source commit.")
        return ReleaseContext(checked_sha(source_sha), "dev", True)
    raise ReleaseError(f"Unsupported release event: {event_name}")


def run_command(arguments: list[str], cwd: Path | None = None) -> str:
    result = subprocess.run(arguments, cwd=cwd, text=True, capture_output=True, check=False)
    if result.returncode:
        detail = result.stderr.strip() or result.stdout.strip()
        raise ReleaseError(f"{arguments[0]} failed ({result.returncode}): {detail}")
    return result.stdout.strip()


def git(source_dir: Path, *arguments: str) -> str:
    return run_command(["git", *arguments], cwd=source_dir)


def checked_sha(value: str) -> str:
    if not re.fullmatch(r"[0-9a-fA-F]{40}", value):
        raise ReleaseError("The source commit must be a full GitHub commit SHA.")
    return value.lower()


def verify_source_identity(source_dir: Path, source_sha: str) -> None:
    message = f"Source commit {source_sha} must declare package identity {PACKAGE_ID} in its project and MCP metadata."
    try:
        project = ET.fromstring(git(source_dir, "show", f"{source_sha}:src/Piston/Piston.csproj"))
        server = json.loads(git(source_dir, "show", f"{source_sha}:src/Piston/Integration/server.json"))
    except (ReleaseError, ET.ParseError, json.JSONDecodeError) as error:
        raise ReleaseError(message) from error

    declarations = [
        (group, element)
        for group in project
        if group.tag.rsplit("}", 1)[-1] == "PropertyGroup"
        for element in group
        if element.tag.rsplit("}", 1)[-1] == "PackageId"
    ]
    packages = server.get("packages") if isinstance(server, dict) else None
    if (
        len(declarations) != 1
        or declarations[0][0].get("Condition") is not None
        or declarations[0][1].get("Condition") is not None
        or (declarations[0][1].text or "").strip() != PACKAGE_ID
        or not isinstance(packages, list)
        or len(packages) != 1
        or not isinstance(packages[0], dict)
        or packages[0].get("identifier") != PACKAGE_ID
    ):
        raise ReleaseError(message)


def read_reservation(source_dir: Path, tag: str, version: ReleaseVersion) -> Reservation:
    ref = f"refs/tags/{tag}"
    if git(source_dir, "cat-file", "-t", ref) != "tag":
        raise ReleaseError(f"Calendar tag {tag} is not an annotated release reservation.")
    tag_object = git(source_dir, "cat-file", "-p", ref)
    headers, separator, body = tag_object.partition("\n\n")
    if not separator:
        raise ReleaseError(f"Calendar tag {tag} has no reservation metadata.")
    fields = dict(line.split(" ", 1) for line in headers.splitlines() if " " in line)
    if fields.get("type") != "commit":
        raise ReleaseError(f"Calendar tag {tag} must point directly to a commit.")
    try:
        metadata = json.loads(body)
    except json.JSONDecodeError as error:
        raise ReleaseError(f"Calendar tag {tag} has invalid reservation metadata.") from error
    if (
        not isinstance(metadata, dict)
        or metadata.get("piston_release") != 1
        or type(metadata.get("run_id")) is not int
        or metadata["run_id"] < 1
        or metadata.get("version") != version.version
        or not isinstance(metadata.get("source_sha"), str)
    ):
        raise ReleaseError(f"Calendar tag {tag} has invalid reservation metadata.")
    source_sha = checked_sha(metadata.get("source_sha", ""))
    if fields.get("object", "").lower() != source_sha:
        raise ReleaseError(f"Calendar tag {tag} does not point to its recorded source commit.")
    has_verified_history = "verified_through" in metadata
    verified_through = metadata.get("verified_through")
    if has_verified_history and verified_through is not None and not isinstance(verified_through, str):
        raise ReleaseError(f"Calendar tag {tag} has invalid verified-history metadata.")
    return Reservation(version, metadata["run_id"], source_sha, has_verified_history, verified_through)


def reservations(source_dir: Path, tags: list[str]) -> list[Reservation]:
    records = []
    counters = {}
    runs = {}
    for tag in tags:
        version = parse_tag(tag)
        if version is None:
            continue
        key = (version.day, version.counter)
        if key in counters:
            raise ReleaseError(f"Calendar counter is reused by {counters[key]} and {tag}.")
        counters[key] = tag
        record = read_reservation(source_dir, tag, version)
        if record.run_id in runs:
            raise ReleaseError(f"Workflow run {record.run_id} reserved both {runs[record.run_id]} and {tag}.")
        runs[record.run_id] = tag
        records.append(record)
    records.sort(key=lambda record: (record.version.day, record.version.counter))
    for index, record in enumerate(records):
        previous_tag = records[index - 1].version.tag if index else None
        if record.has_verified_history and record.verified_through != previous_tag:
            raise ReleaseError(f"Calendar tag {record.version.tag} has an inconsistent release history.")
    return records


class SameOriginRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, url):
        source = urllib.parse.urlsplit(request.full_url)
        destination = urllib.parse.urlsplit(url)
        if (source.scheme, source.netloc) != (destination.scheme, destination.netloc):
            raise ReleaseError("Refusing to send GitHub credentials to a different origin.")
        return super().redirect_request(request, response, code, message, headers, url)


def request_bytes(url: str, token: str | None = None, allow_missing: bool = False) -> bytes | None:
    headers = {
        "User-Agent": "piston-release",
        "Accept": "application/vnd.github+json" if token else "application/octet-stream",
    }
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = urllib.request.Request(url, headers=headers)
    try:
        opener = urllib.request.build_opener(SameOriginRedirect()) if token else urllib.request.build_opener()
        with opener.open(request, timeout=30) as response:
            return response.read()
    except urllib.error.HTTPError as error:
        if allow_missing and error.code == 404:
            return None
        raise ReleaseError(f"Request to {url} failed with HTTP {error.code}.") from error
    except urllib.error.URLError as error:
        raise ReleaseError(f"Request to {url} failed: {error.reason}") from error


def github_json(path: str, allow_missing: bool = False) -> dict | None:
    token = os.environ.get("GH_TOKEN")
    if not token:
        raise ReleaseError("GH_TOKEN is required to inspect release state.")
    api = os.environ.get("GITHUB_API_URL", "https://api.github.com").rstrip("/")
    data = request_bytes(f"{api}{path}", token=token, allow_missing=allow_missing)
    if data is None:
        return None
    try:
        value = json.loads(data)
    except json.JSONDecodeError as error:
        raise ReleaseError(f"GitHub returned invalid JSON for {path}.") from error
    if not isinstance(value, dict):
        raise ReleaseError(f"GitHub returned an unexpected response for {path}.")
    return value


def repo_path(repo: str) -> str:
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repo):
        raise ReleaseError("GITHUB_REPOSITORY must identify an owner and repository.")
    return f"/repos/{repo}"


def public_package(
    version: ReleaseVersion, allow_missing: bool = False, *, package_id: str = PACKAGE_ID,
) -> bytes | None:
    package_version = version.version.lower()
    identifier = package_id.lower()
    if not re.fullmatch(r"[a-z0-9][a-z0-9._-]*", identifier):
        raise ReleaseError("Invalid NuGet package ID.")
    url = (
        f"https://api.nuget.org/v3-flatcontainer/{identifier}/{package_version}/"
        f"{identifier}.{package_version}.nupkg"
    )
    return request_bytes(url, allow_missing=allow_missing)


def package_entries(data: bytes) -> dict[str, bytes]:
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            names = archive.namelist()
            if len(names) != len(set(names)):
                raise ReleaseError("The package contains duplicate archive entries.")
            return {name: archive.read(name) for name in names if not name.endswith("/")}
    except zipfile.BadZipFile as error:
        raise ReleaseError("The package is not a valid NuGet archive.") from error


def package_identity(data: bytes, version: ReleaseVersion, source_sha: str, repo: str) -> None:
    entries = package_entries(data)
    nuspecs = [name for name in entries if name.endswith(".nuspec")]
    if len(nuspecs) != 1 or ".mcp/server.json" not in entries:
        raise ReleaseError("The package must contain one nuspec and MCP metadata.")
    try:
        root = ET.fromstring(entries[nuspecs[0]])
        metadata = next(element for element in root if element.tag.rsplit("}", 1)[-1] == "metadata")
        values = {element.tag.rsplit("}", 1)[-1]: element for element in metadata}
        server = json.loads(entries[".mcp/server.json"])
        packages = server["packages"]
        if not isinstance(server, dict) or not isinstance(packages, list) or len(packages) != 1:
            raise ReleaseError("The package must describe exactly one MCP package.")
        package = packages[0]
        if not isinstance(package, dict):
            raise ReleaseError("The MCP package metadata must be an object.")
    except (ET.ParseError, StopIteration, KeyError, TypeError, json.JSONDecodeError) as error:
        raise ReleaseError("The package metadata is invalid.") from error
    repository = values.get("repository")
    if (
        values.get("id") is None
        or values["id"].text != PACKAGE_ID
        or values.get("version") is None
        or values["version"].text != version.version
        or repository is None
        or repository.get("type") != "git"
        or repository.get("url", "").rstrip("/").lower() != f"https://github.com/{repo}".lower()
        or repository.get("commit", "").lower() != source_sha
        or server.get("version") != version.version
        or package.get("version") != version.version
        or package.get("identifier") != PACKAGE_ID
        or not any(name.endswith("/Piston.dll") for name in entries)
    ):
        raise ReleaseError(f"Package {version.version} does not match its tag, repository, or source commit.")


def compare_packages(expected: bytes, actual: bytes) -> None:
    ignored = {".signature.p7s", "[Content_Types].xml"}
    expected_entries = {name: data for name, data in package_entries(expected).items() if name not in ignored}
    actual_entries = {name: data for name, data in package_entries(actual).items() if name not in ignored}
    if expected_entries != actual_entries:
        raise ReleaseError("The existing package does not match this workflow's package.")


def github_release(repo: str, version: ReleaseVersion, allow_missing: bool = False) -> dict | None:
    tag = urllib.parse.quote(version.tag, safe="")
    release = github_json(f"{repo_path(repo)}/releases/tags/{tag}", allow_missing=allow_missing)
    if release is not None and (
        release.get("tag_name") != version.tag
        or release.get("draft") is not False
        or release.get("prerelease") is not False
    ):
        raise ReleaseError(f"GitHub release {version.tag} is not the expected stable release.")
    return release


def download_github_asset(repo: str, version: ReleaseVersion, filename: str) -> bytes:
    with tempfile.TemporaryDirectory(prefix="piston-release-asset-") as directory:
        run_command(["gh", "release", "download", version.tag, "--repo", repo, "--pattern", filename, "--dir", directory])
        return (Path(directory) / filename).read_bytes()


def verify_public_reservation(record: Reservation, repo: str) -> None:
    data = public_package(record.version)
    if data is None:
        raise ReleaseError(f"Public package {record.version.version} is unavailable.")
    package_identity(data, record.version, record.source_sha, repo)
    if not record.version.dev:
        release = github_release(repo, record.version)
        if release is None:
            raise ReleaseError(f"GitHub release {record.version.tag} is unavailable.")
        expected_name = f"{PACKAGE_ID}.{record.version.version}.nupkg"
        assets = [asset for asset in release.get("assets", []) if asset.get("name") == expected_name]
        if len(assets) != 1 or assets[0].get("state") != "uploaded":
            raise ReleaseError(f"GitHub release {record.version.tag} is missing {expected_name}.")
        compare_packages(data, download_github_asset(repo, record.version, expected_name))


def verify_retired_reservation(record: Reservation, retirement: RetiredReservation, repo: str) -> None:
    if record.run_id != retirement.run_id or record.source_sha != retirement.source_sha:
        raise ReleaseError(f"Retired reservation {record.version.tag} does not match its recorded identity.")

    run = github_json(f"{repo_path(repo)}/actions/runs/{record.run_id}")
    path = run.get("path") if run is not None else None
    if (
        run is None
        or run.get("id") != retirement.run_id
        or not isinstance(path, str)
        or path.split("@", 1)[0] != ".github/workflows/release.yml"
        or run.get("event") != "workflow_run"
        or run.get("head_sha") != retirement.source_sha
        or type(run.get("run_attempt")) is not int
        or run["run_attempt"] != 1
        or run.get("status") != "completed"
        or run.get("conclusion") != "failure"
    ):
        raise ReleaseError(f"Retired reservation {record.version.tag} no longer has the expected failed run.")

    result = github_json(f"{repo_path(repo)}/actions/runs/{record.run_id}/attempts/1/jobs?per_page=100")
    jobs = result.get("jobs") if result is not None else None
    if (
        result is None
        or not isinstance(jobs, list)
        or type(result.get("total_count")) is not int
        or result["total_count"] != len(jobs)
        or any(not isinstance(job, dict) for job in jobs)
    ):
        raise ReleaseError(f"Retired reservation {record.version.tag} has incomplete job evidence.")
    publish_jobs = [job for job in jobs if job.get("name") == "publish"]
    if len(publish_jobs) != 1:
        raise ReleaseError(f"Retired reservation {record.version.tag} has ambiguous publish-job evidence.")
    job = publish_jobs[0]
    steps = job.get("steps")
    if (
        job.get("status") != "completed"
        or job.get("conclusion") != "failure"
        or not isinstance(steps, list)
        or any(not isinstance(step, dict) for step in steps)
    ):
        raise ReleaseError(f"Retired reservation {record.version.tag} has invalid publish-job evidence.")
    login = [step for step in steps if step.get("name") == "Run NuGet/login@v1"]
    publish = [step for step in steps if step.get("name") == "Publish tool to NuGet.org"]
    if (
        len(login) != 1
        or login[0].get("status") != "completed"
        or login[0].get("conclusion") != "failure"
        or len(publish) != 1
        or publish[0].get("status") != "completed"
        or publish[0].get("conclusion") != "skipped"
    ):
        raise ReleaseError(f"Retired reservation {record.version.tag} does not prove publication was skipped.")

    tag = urllib.parse.quote(record.version.tag, safe="")
    if github_json(f"{repo_path(repo)}/releases/tags/{tag}", allow_missing=True) is not None:
        raise ReleaseError(f"Retired reservation {record.version.tag} unexpectedly has a GitHub release.")
    for package_id in (retirement.package_id, PACKAGE_ID):
        if public_package(record.version, allow_missing=True, package_id=package_id) is not None:
            raise ReleaseError(f"Retired reservation {record.version.tag} unexpectedly has a {package_id} package.")
    print(f"Verified retirement of {record.version.tag}: {retirement.reason} Its version remains reserved.")


def verify_previous(record: Reservation, repo: str) -> None:
    retirement = RETIRED_RESERVATIONS.get(record.version.tag)
    if retirement is not None:
        verify_retired_reservation(record, retirement, repo)
        return
    run = github_json(f"{repo_path(repo)}/actions/runs/{record.run_id}", allow_missing=True)
    if run is None:
        workflow = github_json(f"{repo_path(repo)}/actions/workflows/release.yml")
        if workflow is None or workflow.get("path") != ".github/workflows/release.yml":
            raise ReleaseError("Cannot verify that the release workflow is accessible.")
        print(f"Workflow run {record.run_id} is unavailable; verifying its public release.")
    elif (
        run.get("id") != record.run_id
        or run.get("path", "").split("@", 1)[0] != ".github/workflows/release.yml"
        or run.get("status") != "completed"
        or run.get("conclusion") != "success"
    ):
        raise ReleaseError(
            f"Release {record.version.tag} from workflow run {record.run_id} is incomplete. "
            "Resolve and rerun that workflow before publishing another version."
        )
    verify_public_reservation(record, repo)


def verify_history(records: list[Reservation], repo: str) -> None:
    # A checkpoint records that preceding publications or retirements were verified before this tag.
    start = next(
        (index for index in range(len(records) - 1, -1, -1) if records[index].has_verified_history),
        0,
    )
    for record in records[start:]:
        verify_previous(record, repo)


def prepare(source_dir: Path, repo: str, source_sha: str, run_id: int, dev: bool, preview: bool) -> tuple[ReleaseVersion, bool]:
    source_sha = checked_sha(source_sha)
    if run_id < 1:
        raise ReleaseError("The workflow run ID must be positive.")
    if git(source_dir, "cat-file", "-t", source_sha) != "commit":
        raise ReleaseError(f"Source {source_sha} is not a commit.")
    verify_source_identity(source_dir, source_sha)
    git(source_dir, "fetch", "--tags", "--no-recurse-submodules", "origin")
    tags = git(source_dir, "tag", "--list").splitlines()
    if preview:
        return allocate_version(datetime.now(timezone.utc).date(), tags, dev), False

    records = reservations(source_dir, tags)
    own = next((record for record in records if record.run_id == run_id), None)
    if own is not None:
        if own.version.tag in RETIRED_RESERVATIONS:
            raise ReleaseError(f"Release {own.version.tag} is retired and cannot be retried.")
        if own.source_sha != source_sha or own.version.dev != dev:
            raise ReleaseError(f"Workflow run {run_id} already reserved {own.version.tag} for different inputs.")
        if records[-1] != own:
            raise ReleaseError(f"A later release was reserved after {own.version.tag}; refusing an out-of-order retry.")
        if not own.has_verified_history:
            verify_history(records[:-1], repo)
        return own.version, True

    if records:
        if datetime.now(timezone.utc).date() < records[-1].version.day:
            raise ReleaseError(f"UTC date precedes the latest release reservation {records[-1].version.tag}.")
        verify_history(records, repo)
    # Verification can cross midnight; allocate against the day the tag is created.
    today = datetime.now(timezone.utc).date()
    if records and today < records[-1].version.day:
        raise ReleaseError(f"UTC date precedes the latest release reservation {records[-1].version.tag}.")
    version = allocate_version(today, tags, dev)
    metadata = json.dumps(
        {
            "piston_release": 1,
            "run_id": run_id,
            "source_sha": source_sha,
            "version": version.version,
            "verified_through": records[-1].version.tag if records else None,
        },
        separators=(",", ":"),
    )
    git(
        source_dir,
        "-c", "user.name=github-actions[bot]",
        "-c", "user.email=41898282+github-actions[bot]@users.noreply.github.com",
        "tag", "-a", version.tag, source_sha, "-m", metadata,
    )
    git(source_dir, "push", "origin", f"refs/tags/{version.tag}")
    return version, False


def is_reserved_retry(reused: bool, artifact: str, run_id: int, run_attempt: int) -> bool:
    if run_id < 1 or run_attempt < 1:
        raise ReleaseError("The workflow run ID and attempt must be positive.")
    match = re.fullmatch(rf"piston-nuget-tool-{run_id}-([1-9][0-9]*)", artifact)
    if match is None or int(match.group(1)) > run_attempt:
        raise ReleaseError("The package artifact does not belong to this workflow run and attempt.")
    # A successful package job may be reused unchanged when only later jobs are retried.
    return reused or int(match.group(1)) < run_attempt


def reconcile(package_path: Path, version: ReleaseVersion, source_sha: str, repo: str, retry: bool) -> bool:
    local = package_path.read_bytes()
    package_identity(local, version, source_sha, repo)
    existing = public_package(version, allow_missing=True)
    if existing is None:
        return True
    if not retry:
        raise ReleaseError(f"NuGet already contains {version.version}; refusing an unrelated duplicate.")
    package_identity(existing, version, source_sha, repo)
    compare_packages(local, existing)
    print(f"NuGet already contains the matching {version.version} package; resuming verification.")
    return False


def verify_public_package(package_path: Path, version: ReleaseVersion, source_sha: str, repo: str) -> None:
    local = package_path.read_bytes()
    package_identity(local, version, source_sha, repo)
    public = public_package(version)
    if public is None:
        raise ReleaseError(f"Public package {version.version} is unavailable.")
    package_identity(public, version, source_sha, repo)
    compare_packages(local, public)


def ensure_github_release(package_path: Path, version: ReleaseVersion, source_sha: str, repo: str) -> None:
    if version.dev:
        raise ReleaseError("A dev package must not create a GitHub Release.")
    local = package_path.read_bytes()
    package_identity(local, version, source_sha, repo)
    release = github_release(repo, version, allow_missing=True)
    if release is None:
        run_command(["gh", "release", "create", version.tag, "--repo", repo, "--verify-tag", "--generate-notes"])
        release = github_release(repo, version)
    if release is None:
        raise ReleaseError(f"GitHub release {version.tag} is unavailable after creation.")
    filename = package_path.name
    assets = [asset for asset in release.get("assets", []) if asset.get("name") == filename]
    if len(assets) > 1:
        raise ReleaseError(f"GitHub release {version.tag} has duplicate {filename} assets.")
    if assets:
        if assets[0].get("state") != "uploaded":
            raise ReleaseError(f"GitHub release asset {filename} is not fully uploaded.")
        compare_packages(local, download_github_asset(repo, version, filename))
    else:
        run_command(["gh", "release", "upload", version.tag, str(package_path), "--repo", repo])


def write_outputs(**outputs: str) -> None:
    output_path = os.environ.get("GITHUB_OUTPUT")
    for name, value in outputs.items():
        print(f"{name}={value}")
    if output_path:
        with open(output_path, "a", encoding="utf-8") as output:
            for name, value in outputs.items():
                output.write(f"{name}={value}\n")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    context = commands.add_parser("context")
    context.add_argument("--event-name", required=True)
    context.add_argument("--ref", required=True)
    context.add_argument("--workflow-sha", required=True)
    context.add_argument("--event-file", type=Path, required=True)
    context.add_argument("--repo", required=True)
    context.add_argument("--manual-publish", choices=("true", "false"), default="false")
    reserve = commands.add_parser("prepare")
    reserve.add_argument("--source-dir", type=Path, required=True)
    reserve.add_argument("--source-sha", required=True)
    reserve.add_argument("--repo", required=True)
    reserve.add_argument("--run-id", type=int, required=True)
    reserve.add_argument("--kind", choices=("dev", "stable"), required=True)
    reserve.add_argument("--preview", action="store_true")
    check = commands.add_parser("reconcile")
    release = commands.add_parser("github-release")
    public = commands.add_parser("verify-package")
    for command in (check, release, public):
        command.add_argument("--package", type=Path, required=True)
        command.add_argument("--version", required=True)
        command.add_argument("--source-sha", required=True)
        command.add_argument("--repo", required=True)
    check.add_argument("--reused", choices=("true", "false"), required=True)
    check.add_argument("--artifact", required=True)
    check.add_argument("--run-id", type=int, required=True)
    check.add_argument("--run-attempt", type=int, required=True)
    arguments = parser.parse_args()
    repo_path(arguments.repo)
    if arguments.command == "context":
        try:
            event = json.loads(arguments.event_file.read_text(encoding="utf-8"))
        except json.JSONDecodeError as error:
            raise ReleaseError("GitHub event payload is invalid JSON.") from error
        if not isinstance(event, dict):
            raise ReleaseError("GitHub event payload must be an object.")
        selected = select_context(
            arguments.event_name, arguments.ref, arguments.repo, arguments.workflow_sha,
            event, arguments.manual_publish == "true",
        )
        write_outputs(source_sha=selected.source_sha, kind=selected.kind, publish=str(selected.publish).lower())
        return
    if arguments.command == "prepare":
        version, reused = prepare(
            arguments.source_dir, arguments.repo, arguments.source_sha, arguments.run_id,
            arguments.kind == "dev", arguments.preview,
        )
        write_outputs(version=version.version, tag=version.tag, reused=str(reused).lower())
        return
    version = parse_tag(f"v{arguments.version}")
    if version is None:
        raise ReleaseError(f"Invalid calendar package version: {arguments.version}")
    source_sha = checked_sha(arguments.source_sha)
    if arguments.command == "reconcile":
        retry = is_reserved_retry(
            arguments.reused == "true", arguments.artifact, arguments.run_id, arguments.run_attempt,
        )
        push = reconcile(arguments.package, version, source_sha, arguments.repo, retry)
        write_outputs(push=str(push).lower())
    elif arguments.command == "verify-package":
        verify_public_package(arguments.package, version, source_sha, arguments.repo)
    else:
        ensure_github_release(arguments.package, version, source_sha, arguments.repo)


if __name__ == "__main__":
    try:
        main()
    except (ReleaseError, ValueError, OSError) as error:
        print(f"::error::{error}", file=sys.stderr)
        sys.exit(1)
