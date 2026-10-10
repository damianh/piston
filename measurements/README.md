# Measurement harness (Phase 0)

This is **baseline instrumentation, not evidence that Piston is faster**. It
launches ordinary `dotnet test` in isolated fixture copies, verifies
the full test identity/status set, and records source provenance, monotonic
edit-to-result timing, sampled owned-process resources and raw evidence. It does
not implement engine features or launch agents. All scope is under this directory.

## Authorized validation

Requires Linux, Python 3.10+ and exactly .NET SDK 10.0.401. Fixture configuration
and NuGet lock files are independent of the root solution. No Docker is needed for
these commands:

```sh
python3 -m unittest discover -s measurements -p 'test_*.py'
dotnet restore measurements/fixtures/unit/Orders.slnx --locked-mode
dotnet restore measurements/fixtures/integration/Persistence.slnx --locked-mode
dotnet build measurements/fixtures/integration/Persistence.slnx --no-restore
python3 measurements/harness.py --scenario baseline --output measurements/artifacts/baseline
python3 measurements/harness.py --scenario regression --output measurements/artifacts/regression
python3 measurements/harness.py --scenario repair --output measurements/artifacts/repair
```

Use a new output directory for every run; existing directories are rejected.
Additional unit smoke scenarios: `rounding`, `known-failure`,
`regression-with-existing-failure`, `compile-error`.
The unit fixture has 409 real assertions/cases across two test and two production
projects. It exercises price boundaries, shared rounding, order validation and
inventory. Its small production code is intentionally reproducible, not a proxy
for every large repository. Report observed runtime rather than calling it a
large-workload result.

Every trial first restores locked dependencies and runs a full baseline.
It then applies the scenario edit and invokes the same full-scope test command
with incremental build enabled. `repair` first executes and verifies the regression,
then times its repair. The scenarios specify exactly which distinct sentinel
identities should fail; all other identities/statuses must match the baseline.
An expected failing test command is a **correct result**, not a contention incident.
`regression-with-existing-failure` seeds and verifies a distinct pre-existing
failure before timing a new regression; the oracle must preserve both. The
`known-failure` scenario merely demonstrates introducing that sentinel, not agent
effort in investigating an existing failure.
Compile-error trials check CS0103 and absence of test execution and are summarized
separately. Timing ends at command completion, before offline oracle validation;
source hashing and result capture overhead after edit are included.

## Explicitly permitted later runs only

Repeated or concurrent campaigns require operator approval, available host
capacity, and `--approve-campaign`. For example, after approval:

```sh
python3 measurements/harness.py --scenario regression --repetitions 5 \
  --concurrency 2 --approve-campaign --output measurements/artifacts/pilot
```

Workers have independent source, build and TRX directories and synchronize **after**
their baseline/setup, before applying the edit. Concurrency is 1, 2 or 4; inspect
CPU/memory/cgroup limits before choosing 4. These are scripted concurrent commands,
not real multi-agent sessions. A timeout or failed setup aborts the shared barrier
and is retained as an invalid attempted sample. No shared scheduler benefit is
implied. No host-wide stress or resource exhaustion is intentional.

The PostgreSQL/Testcontainers fixture tests idempotency, uniqueness, rollback and
concurrent updates, plus a distinct known-failure sentinel. Its container uses a
random mapped port, unique database, per-trial label, 1 CPU and 512 MiB limit.
**Do not execute it without separate permission for Docker, image download,
resource use and cleanup.** The image digest is deliberately unresolved until
that permission: select an approved PostgreSQL version/platform, record its
immutable registry digest, and set:

```sh
export DOCKER_HOST=unix://<approved-local-api-socket>
export MEASUREMENT_POSTGRES_IMAGE=postgres@sha256:<approved-64-hex-digest>
python3 measurements/harness.py --workload integration --scenario regression \
  --approve-containers --output measurements/artifacts/postgres-regression
```

The runner rejects mutable tags, remote API sockets and absent consent.
The Unix socket must also be compatible with Docker.DotNet (shorter than its
108-byte endpoint limit; avoid a leading-dot filename). A rootless Podman API
socket is supported; set `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE` to the
host-visible socket path when needed for Ryuk's bind mount.
Testcontainers owns cleanup;
retain Ryuk rather than disabling its reaper. On interrupted/failed container
execution the operator must inspect **only** resources labeled
`piston.measurement.trial=<trial-id>`, preserve evidence and remove those explicit
IDs. Trial IDs include a random UUID to prevent ownership collisions across
campaigns. The runner observes only exact trial-labeled containers through the
local Docker-compatible API: image ID, CPU/memory limits, cumulative CPU and
memory usage, with baseline/preparation/edited phases. It polls every 250 ms
plus API latency (each request has a three-second timeout). Missing samples and
telemetry errors are explicit, not zero usage.

After execution it confirms no trial-labeled containers remain. Any leftovers
are inspected again for the exact label before their explicit IDs are removed;
required fallback cleanup is recorded as an infrastructure incident. Failed
cleanup invalidates the trial. Container memory usage is not process RSS;
samples can miss startup/teardown peaks, and observation adds overhead.
Compilation without execution is not an integration smoke result.

## Artifacts and limitations

`manifest.json` records fixture hash, SDK, scenario, scope, host context and
permissions. Each trial retains its source copy, command logs, timestamped TRX,
resource samples, events and trial record. `trials.jsonl` and `summary.json`
include all attempted samples, correctness failures, timeouts, median and
nearest-rank p95. Raw artifacts are gitignored: preserve them outside Git and only
publish sanitized, approved evidence. Workspaces remain for diagnosis; remove
only specific completed experiment directories, not the repository or artifact
root. Runtime logs may contain local paths.

CPU is the maximum sum of sampled live owned-process CPU, a **lower bound**, not
complete process-tree accounting. RSS is a sampled peak; short-lived children
may be missed. `/proc` inspection is confined to launched subprocess descendants.
Host load, pressure and memory, and observable ancestor cgroup limits, are
recorded as context on a shared host. High CPU does not itself establish a
contention failure. Stage-level build/test timings and agent metrics are null
with reasons when unobservable; unit trials have no container statistics.

Only dotnet is executable as a condition initially. Existing MCP latest-state
aggregates are not a valid generation-bound full-scope oracle. A future current-
Piston adapter must first prove fresh per-test results on the exact edited source
and preserve all test identities; otherwise its condition stays blocked. No
backend latency improvement, paired confidence interval or cognitive benefit can
be computed from baseline-only runs. The approved local G0 campaign and its
scope-limited recommendation are recorded in [results/g0-local.md](results/g0-local.md).
See [methodology.md](methodology.md)
and [agent-experiments.md](agent-experiments.md) for the comparison protocol.
