# Measurement harness

This is **baseline instrumentation, not evidence that Piston is faster**. It
launches ordinary `dotnet test` in isolated fixture copies, verifies
the full test identity/status set, and records source provenance, monotonic
edit-to-result timing, sampled owned-process resources and raw evidence. It does
not implement engine features or launch agents. The optional Piston condition
starts a real, isolated daemon from a prebuilt DLL. All harness code is under this directory.

## Authorized validation

Requires Linux and .NET SDK 10.0.401. The harness is a .NET console application
with xUnit self-tests; Python is not required. Its separate `Measurements.slnx`
does not build the Piston engine; its real-daemon tests are opt-in. Fixture configuration and NuGet lock
files are independent of the root solution. No Docker is needed for these commands:

```sh
dotnet restore measurements/Measurements.slnx --locked-mode
dotnet test measurements/Measurements.slnx --no-restore
dotnet build measurements/Runner/Runner.csproj -c Release --no-restore
dotnet restore measurements/fixtures/unit/Orders.slnx --locked-mode
dotnet restore measurements/fixtures/integration/Persistence.slnx --locked-mode
dotnet build measurements/fixtures/integration/Persistence.slnx --no-restore
dotnet measurements/Runner/bin/Release/net10.0/Piston.Measurements.dll \
  --scenario baseline --output measurements/artifacts/baseline
dotnet measurements/Runner/bin/Release/net10.0/Piston.Measurements.dll \
  --scenario regression --output measurements/artifacts/regression
dotnet measurements/Runner/bin/Release/net10.0/Piston.Measurements.dll \
  --scenario repair --output measurements/artifacts/repair
```

Build the runner once before measuring, then execute the prebuilt assembly.
Do not use `dotnet run` in measurement loops: it adds a runner build and changes
MSBuild state. The dotnet condition invokes MSBuild only for fixture restore/test commands.
The Piston condition additionally invokes the prebuilt daemon's normal build/test path.
The runner's .NET runtime and polling still add overhead;
the manifest records the runner runtime, assembly hash and prebuilt policy.

Use a new output directory for every run; existing directories are rejected.
Trailing separators are normalized. New experiment roots are created atomically
with mode `0700` to protect raw artifacts on a shared host; existing historical
experiment permissions are not changed automatically.
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
dotnet measurements/Runner/bin/Release/net10.0/Piston.Measurements.dll \
  --scenario regression --repetitions 5 \
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
dotnet measurements/Runner/bin/Release/net10.0/Piston.Measurements.dll \
  --workload integration --scenario regression \
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
Samples spanning a phase transition are discarded rather than attributed to
the later command. Telemetry errors retain the request-start phase and include
`crossed_phase_transition` when the phase generation changed during the request,
including transitions back to the same phase. A cleanup DELETE returning 404 is not counted as a harness
removal; the final exact-label query must still confirm an empty result.

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

Both `--condition dotnet` (the default) and `--condition piston` are executable.
Piston currently supports only the unit fixture's six per-test scenarios;
integration and `compile-error` are rejected before output creation.
MCP latest-state aggregates
(`get_test_results`, `run_tests` counts) are not a valid generation-bound
full-scope oracle. Piston now exposes per-run provenance through `get_test_run`.
`PistonRunOracle` accepts a run only if all of these hold:

- it has the requested run ID;
- it is `Completed`;
- it is unfiltered `Full` scope;
- its solution path matches the trial's isolated workspace;
- it is `Current`, with no source change observed;
- its before and after `piston-source-v2` fingerprints equal the fingerprint the harness computed for the edited workspace;
- it reports exactly the expected unique test identities and statuses.

It rejects summary-only, missing, extra, duplicate or mismatched identities.
Expected assertion failures are correct results; cancelled, build-failed,
faulted or incomplete runs are not.

### Piston trial condition

Build the daemon and runner before any trial. One unit correctness smoke can be
invoked with a new output directory:

```sh
dotnet build Piston.slnx
dotnet build measurements/Runner/Runner.csproj -c Release
dotnet measurements/Runner/bin/Release/net10.0/Piston.Measurements.dll \
  --condition piston --piston-daemon-dll "$PWD/src/Piston/bin/Debug/net10.0/Piston.dll" \
  --scenario regression --output measurements/artifacts/piston-regression-smoke
```

Each trial restores and establishes the same plain-dotnet full identity/status
baseline in its isolated copy. Repair preparation and seeded existing failures
are unchanged. Before the edit barrier, it starts a daemon with unique local
ports, pipe and configuration directory, sets test-process parallelism to 1 (the
single-condition default; paired mode matches it, see below), and
validates its startup full run against that pre-edit source and expected results.
Each daemon gets its own new `PISTON_EXECUTION_DIRECTORY` (`piston-execution`
inside the trial artifacts, with no `budget.json`), so machine-wide admission
never shares a queue or budget with the operator's own daemons or with other
trials; the engine's default budget (one CPU slot per logical CPU) applies.
For operator-managed budgets outside these trials, update `budget.json` by writing
complete JSON to a temporary file in the same directory, then atomically renaming
it over the existing file without deleting the old file first; avoid in-place edits.
Cross-daemon admission and scheduling are therefore **not** exercised. Each
accepted run's admission record is retained, and `piston_admission_isolated` is
true only when it was admitted alone against that default budget with the
launched reservation.
Daemon startup/warming is setup, not edit latency. Port reservation is best
effort: bind/start failures invalidate the attempt rather than retrying it.

Watching stays enabled. Edit latency includes the watcher-triggered run settling,
then exactly one `run_tests` call and `get_test_run` for the returned run ID.
The watcher result is retained but **never** accepted as the full-scope trial.
No retry or dotnet fallback rescues a failed forced run. Timing ends after the
run-bound response and raw capture, before offline oracle validation and cleanup.
The harness computes the edited solution fingerprint independently and checks
it again after execution, as well as comparing the fixture source hash.
Generated `.piston` state is excluded from fixture copying/source hashes.

Trials retain daemon launch arguments/hash, setup and watcher run documents,
the MCP response, full run document, daemon logs, measured-phase owned-process
samples and cleanup evidence. `piston_execution` records the run ID and observed
resources; `execution` is null because an MCP result has no test-command exit
code. Resource sampling includes the live daemon and its observable descendants
during the edited phase only, not complete lifetime CPU or startup resource cost.
The owned daemon tree is terminated before the trial is persisted; a cleanup
error invalidates even an oracle-correct result. Timeouts and MCP errors remain
invalid attempted samples. Campaign/container consent guards are unchanged.

An opt-in end-to-end smoke exercises this against a real daemon's HTTP MCP endpoint on the unit fixture. It covers:

- a clean full run;
- rejection of a prior run;
- a generation mismatch after an edit;
- rejection of the selective file-change run;
- a regression full run;
- rejection of a filtered run;
- a source edit during a run (the run is cancelled or stale).

Run it with:

```sh
dotnet build Piston.slnx
PISTON_DAEMON_DLL=$PWD/src/Piston/bin/Debug/net10.0/Piston.dll \
  dotnet test measurements/Runner.Tests \
    --filter 'FullyQualifiedName~PistonOracleSmokeTests|FullyQualifiedName~PistonTrialSmokeTests'
```

The trial smoke exercises the actual harness with a regression plus a distinct
existing failure, all 409 identities, and daemon cleanup. Both smokes are opt-in
through `PISTON_DAEMON_DLL`; ordinary self-tests launch no daemon.
These are still missing:

- an executed paired campaign (the paired mode below exists but no repeated
  campaign or pilot has been approved or run);
- the PostgreSQL workload for Piston (and so any PostgreSQL pairing);
- a Piston compile-diagnostic trial condition (and so any compile-error pairing);
- a larger repository;
- telemetry-overhead measurement.

### Paired comparison mode

`--paired` runs both conditions as counterbalanced pairs on the unit fixture;
`--repetitions N` is the number of pairs and `--concurrency` the trials per
condition batch. It reuses the unchanged dotnet and Piston trial paths and
`PistonRunOracle`. A single pair at concurrency 1 is a bounded correctness smoke;
more pairs or concurrency are a campaign and still require `--approve-campaign`
(and operator approval):

```sh
dotnet measurements/Runner/bin/Release/net10.0/Piston.Measurements.dll \
  --paired --seed 42 --scenario regression \
  --piston-daemon-dll "$PWD/src/Piston/bin/Debug/net10.0/Piston.dll" \
  --output measurements/artifacts/paired-regression-smoke
```

- `--paired` cannot be combined with `--condition`; `--seed` requires `--paired`.
  Integration (PostgreSQL) and `compile-error` are rejected before output creation
  rather than falling back to a single condition.
- The order plan is fixed before any trial: balanced dotnet-first/piston-first
  counts (an odd pair decided by the seed), shuffled with the seed. The seed is
  operator-supplied or generated, and recorded with the full plan, pair IDs and
  batch IDs in `manifest.json`.
- Pairs share workload, scenario, repetition and concurrency. Condition batches
  run sequentially and never overlap; each batch keeps its own edit barrier and
  independent workspaces. Every trial records `pair_id` and `pair_position`.
- Allocation: the dotnet condition keeps its default command (MSBuild node count =
  logical CPUs). Its nominal test-process concurrency is min(baseline TRX files,
  logical CPUs). In paired mode Piston's `--parallelism` is set to that value.
  The match is nominal: the harness does not enforce OS-level CPU allocation.
- Cache: both conditions restore and run a full plain-dotnet baseline in a fresh
  copy; Piston additionally completes its validated startup run. MSBuild node
  reuse, the compiler server, the OS page cache and NuGet caches are shared and
  not reset, so matched cache state is **not** enforced. Counterbalancing spreads
  carry-over between conditions without removing it.
- `summary.json` keeps all attempted, invalid and timeout counts per condition.
  `paired.pairs` reports each pair's batches, trial IDs and problems. A pair is
  usable only if both batches are complete and correct. The base, baseline,
  pre-edit and edited source hashes, baseline identity hash and expected statuses
  must be identical. Test-process parallelism must match, oracles must be full
  scope, and Piston admission must be isolated.
- `paired_improvement` is computed only when **every** planned pair is usable, at
  least two pairs exist and the run was not cancelled. No pair is dropped to
  rescue statistics. Otherwise it is null and `statistics_withheld_reasons`
  says why. The unit of analysis is the pair of condition batch means, with a
  pair-resampled bootstrap using the recorded seed. Concurrent trials are never
  treated as independent. The exit code is nonzero if any attempt is invalid or
  any pair is unusable.

Paired output is scripted-command evidence, not a real-agent result. It is not
a G1 decision (`summary.G1` is `not evaluated`).

The oracle smoke is a correctness check, not a timing sample; the harness smoke's
trial timing is functional evidence only, not comparison-campaign evidence. No
backend latency improvement, paired confidence interval or cognitive benefit can
be computed from baseline-only runs. A single-pair paired smoke is functional evidence only and never yields
statistics. The approved local G0 campaign and its
scope-limited recommendation are recorded in [results/g0-local.md](results/g0-local.md).
Those historical campaigns used the original Python runner; port-validation
smokes are separate and do not replace or augment the campaign sample counts.
See [methodology.md](methodology.md)
and [agent-experiments.md](agent-experiments.md) for the comparison protocol.
