# Measurement harness (Phase 0)

This is **baseline instrumentation, not evidence that Piston is faster**. G0 is
pending. It launches ordinary `dotnet test` in isolated fixture copies, verifies
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
export MEASUREMENT_POSTGRES_IMAGE=postgres@sha256:<approved-64-hex-digest>
python3 measurements/harness.py --workload integration --scenario regression \
  --approve-containers --output measurements/artifacts/postgres-regression
```

The runner rejects mutable tags and absent consent. Testcontainers owns cleanup;
retain Ryuk rather than disabling its reaper. On interrupted/failed container
execution the operator must inspect **only** resources labeled
`piston.measurement.trial=<trial-id>`, preserve evidence and remove those explicit
IDs. Container stats and confirmed cleanup are not currently collected by the
runner; container campaigns cannot establish the resource/no-contention goal
until these observations are added. Compilation without execution is not an
integration smoke result.

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
Host memory is contextual on a shared host. High CPU does not itself establish a
contention failure. Stage-level build/test timings, container statistics and agent
metrics are null with reasons when unobservable.

Only dotnet is executable as a condition initially. Existing MCP latest-state
aggregates are not a valid generation-bound full-scope oracle. A future current-
Piston adapter must first prove fresh per-test results on the exact edited source
and preserve all test identities; otherwise its condition stays blocked. No
latency percentage, confidence interval, G0 pass or cognitive benefit can be
computed from these baseline-only smoke runs. See [methodology.md](methodology.md)
and [agent-experiments.md](agent-experiments.md) for the comparison protocol.
