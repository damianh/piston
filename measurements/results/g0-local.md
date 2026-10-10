# G0: local scripted dotnet baseline

## Decision scope

This report measures the cost of ordinary, full-scope `dotnet test` on two local
fixtures. It is not a Piston speed comparison, a real-agent experiment, or a
population-wide result. G0 asks whether baseline costs justify investigating a
service; G1 must separately establish whether that service improves them.

## Environment and provenance

The campaign ran on 2026-10-10 on a shared Linux x86-64 host: NixOS 26.11,
kernel 6.18.55, 16 logical CPUs, 61.90 GiB physical RAM, no swap. Python was
3.14.7; the fixture SDK was 10.0.401, MSBuild 18.9.11, and the selected .NET
runtime was 10.0.12. Fixture packages were restored using committed lock files.
All observable runner cgroup ancestors had unlimited `cpu.max`, `memory.max`
and `memory.high`; this is not a reserved host allocation.

Fixture revision: `58aa6ac55c7a68069f2b3002049ae3d8bf7aa520`.
Combined fixture SHA-256:
`585a57913afde1805a8a39b437091b9b2961adc1fc0d6b964ecc1fa5f518cc05`.
Instrumented runner SHA-256 during the campaigns:
`0eba93d6f1ecbbe7699e3d27c3ebb39f8c157320e9f42fd5b9f4a0206bb4719d`.
The fixtures and runner configuration were unchanged throughout the statistical
campaigns. No MSBuild node-reuse environment override was applied.

Rootless Podman 5.8.7 provided the Docker-compatible API. The configured user
socket was reported active but its pathname was absent, so a campaign-owned
local Unix socket was used. Two setup smoke attempts failed before PostgreSQL
started: Docker.DotNet rejected an overlong socket path, then a leading-dot
socket filename caused a URI error. A short, non-dot filename resolved both.
Those attempts are retained as invalid samples, not Piston contention.

The official `docker.io/library/postgres:18` image resolved to PostgreSQL
`18.6-1.pgdg13+2` on linux/amd64. Every measured integration invocation used:

```text
MEASUREMENT_POSTGRES_IMAGE=postgres@sha256:885953109528ad3dfc90362b1a6f50a78620b5315be19f187753d267e484dc5b
```

Local image config ID:
`sha256:29754c7520f4b9654cf8ff98e19ac73b88e255c3a53f6236ebe9d52249bb6df3`.
Registry/repository inspection recorded the immutable digest above; image pulls
were setup, outside measured edit latency. An initial explicit 18.3 image pull
was superseded by the current major tag before any valid integration run.
Ryuk remained enabled. PostgreSQL retained the fixture's unique database,
random host port, exact trial label, 1 CPU and 512 MiB limits.

## Design and interpretation

Each trial used an independent source/build/TRX directory, locked restore and
full baseline. Repair also verified its preparatory regression. Workers met at
the edit barrier only after setup. Timing began after writing the scenario edit
and ended when the full test command completed, before offline oracle checking;
source hashing and 50 ms process polling were included. A fresh full identity/
status set and unchanged edit hash were required. Intentional assertion failures
and the expected nonzero exit are correct results, not incidents.

Five pilot batches per scenario at concurrency 1 preceded twenty independent
batches per selected scenario/concurrency. At concurrency 2 and 4, each batch
contained two and four workers respectively. Scenario order within each
concurrency stage was shuffled using seed `20261010`; concurrency stages were
ordered 1, 2, 4, not randomized or paired with a backend.

The initial schedule unnecessarily expanded eight scenarios to every concurrency
level. After the user challenged its elapsed time, the scheduler was paused
without interrupting the active integration-repair campaign, then stopped at its
completed campaign boundary. Remaining work was narrowed to regression at
concurrency 4 for both workloads, plus the outstanding unit regression at
concurrency 2. Other concurrency-4 scenarios were never launched. This amendment
is retained in the raw evidence; no completed slower samples were discarded.

Before each campaign, capacity checks required one-minute load below 12 and at
least 12 GiB available RAM. Checks deferred launches in 30-second increments
rather than stressing the shared host. Across 26 launched pilot/campaign groups,
approved prelaunch one-minute load ranged from 4.22 to 11.96 and available RAM
from 42.83 to 45.81 GiB. There were 31 deferred checks (15.5 minutes of deliberate
waiting); one deferred snapshot immediately after the unit concurrency-4 campaign
had load 37.78. A low prelaunch load did not ensure low pressure during execution.
No concurrency above 4, external clone,
paid agent session, or package publication was performed.

## Counts, correctness and incidents

| Evidence set | Groups | Batches | Attempted | Valid | Invalid | Command timeouts |
|---|---:|---:|---:|---:|---:|---:|
| Unit pilots, including compile diagnostics | 5 | 25 | 25 | 25 | 0 | 0 |
| PostgreSQL pilots | 3 | 15 | 15 | 15 | 0 | 0 |
| Unit campaigns, including compile diagnostics | 11 | 220 | 380 | 380 | 0 | 0 |
| PostgreSQL campaigns | 7 | 140 | 260 | 260 | 0 | 0 |
| Integration setup smokes | 3 | 3 | 3 | 1 | 2 | 0 |
| **Total** | **29** | **403** | **683** | **681** | **2** | **0** |

Of the 681 valid samples, 616 verified full test identity/status sets and 65
verified compile diagnostics without test execution. All selected cells contain
every attempted sample; none were dropped for being slow. The two invalid
samples are the socket configuration failures described above, outside the
campaign distributions. There were no test-oracle disagreements, test-command
timeouts, port collisions, container-start failures or fallback cleanup incidents
in the 640 statistical campaign trials. This is zero observed execution failures
under these controls, not proof that contention is absent or impossible.

The long wall-clock campaign was not one hung test: pilots/campaigns ran from
09:01 to 10:49 UTC, including capacity waits. Across all fixture trials there were
683 restores, 683 baseline commands, 130 repair-preparation commands and 681
edited commands: **2,177 dotnet subprocess launches**. Repeated setup and the
initial excessive scenario matrix explain the elapsed work; headline edit
latency excludes that setup. No individual command reached the 180-second
timeout. Raw retained evidence occupies approximately 9.2 GiB.

## Edit-to-correct-result latency

All values below are **seconds, median / nearest-rank p95**, rounded only for
display. Each measured cell has 20 batches: 20 workers at concurrency 1, 40 at
concurrency 2 and 80 at concurrency 4. The concurrency-4 dash means not run,
not zero. Compile diagnostics are deliberately separated from test results.

| Workload / scenario | Concurrency 1 | Concurrency 2 | Concurrency 4 |
|---|---:|---:|---:|
| Unit: localized regression | 2.734 / 2.939 | 3.734 / 3.875 | 6.214 / 6.554 |
| Unit: repair | 3.015 / 4.857 | 3.623 / 4.463 | -- |
| Unit: shared rounding dependency | 2.731 / 3.199 | 3.364 / 4.244 | -- |
| Unit: regression with existing failure | 2.962 / 4.324 | 3.285 / 3.784 | -- |
| PostgreSQL: idempotency regression | 6.869 / 7.340 | 9.453 / 13.029 | 13.814 / 17.303 |
| PostgreSQL: repair | 5.882 / 7.128 | 11.867 / 13.309 | -- |
| PostgreSQL: regression with existing failure | 5.602 / 6.764 | 7.182 / 9.000 | -- |

| Separate diagnostics oracle | Concurrency 1 | Concurrency 2 | Concurrency 4 |
|---|---:|---:|---:|
| Unit: compile error, CS0103, no test execution | 1.350 / 1.427 | 1.649 / 2.070 | -- |

Pilot results are not pooled into these headline cells. Each pilot below had
five attempted/valid samples in five concurrency-1 batches, no timeouts, and no
test-oracle disagreements. With five samples, nearest-rank p95 is the maximum.

| Pilot scenario | Median / p95 seconds |
|---|---:|
| Unit regression | 2.633 / 2.839 |
| Unit repair | 2.632 / 3.392 |
| Unit shared rounding | 2.738 / 2.788 |
| Unit regression with existing failure | 2.636 / 3.680 |
| Unit compile diagnostics | 1.272 / 1.327 |
| PostgreSQL regression | 5.753 / 6.098 |
| PostgreSQL repair | 9.386 / 12.768 |
| PostgreSQL regression with existing failure | 5.753 / 6.966 |

The successful setup smoke's single 6.976-second regression result is also
excluded from the statistical cells. The slow PostgreSQL repair pilot remains
visible rather than being removed as an outlier.

### Scaling and batch-level observations

Localized regression worker medians rose by **1.37x at concurrency 2 and 2.27x
at concurrency 4** for unit tests, relative to concurrency 1. PostgreSQL medians
rose by **1.38x and 2.01x**. These ratios describe observed end-to-end command
latency, not a backend improvement or a causal attribution to CPU contention.
Unit p95 rose from 2.939 to 6.554 seconds; PostgreSQL p95 rose from 7.340 to
17.303 seconds. Other scenarios were not extrapolated to concurrency 4.

The following uses **20 batch observations per cell**, not 40/80 independent
replicates. Batch mean averages its simultaneous workers; slowest-worker timing
describes waiting for every worker in that batch. Values are median / p95 seconds.

| Regression batch measure | Concurrency 1 | Concurrency 2 | Concurrency 4 |
|---|---:|---:|---:|
| Unit: mean worker latency | 2.734 / 2.939 | 3.731 / 3.858 | 6.235 / 6.477 |
| Unit: slowest worker | 2.734 / 2.939 | 3.756 / 3.889 | 6.375 / 6.554 |
| PostgreSQL: mean worker latency | 6.869 / 7.340 | 9.924 / 12.399 | 13.909 / 16.487 |
| PostgreSQL: slowest worker | 6.869 / 7.340 | 10.665 / 13.030 | 15.416 / 18.386 |

Setup is separate. For regression at concurrency 1/2/4, median locked restore
was 1.109/1.214/1.468 seconds for unit and 1.210/1.161/1.416 seconds for
PostgreSQL. Median full-baseline command time was 3.087/4.013/6.591 seconds for
unit and 7.163/10.446/13.249 seconds for PostgreSQL. These are setup in newly
copied workspaces, not cold-machine startup, and are not pooled with edit loops.

### CPU, memory and cleanup

The following observations exclude compile diagnostics and pilots. Concurrency
1/2 rows include all full-test scenarios; concurrency 4 includes only regression.
CPU columns are seconds of observed cumulative CPU lower bound per edited
command/container. RSS/usage columns are MiB of sampled peak per command/container.
Values are median / p95, not complete resource accounting.

| Workload | Concurrency | Valid trials | Owned-process CPU s | Owned-process peak RSS MiB | PostgreSQL CPU s | PostgreSQL peak usage MiB |
|---|---:|---:|---:|---:|---:|---:|
| Unit | 1 | 80 | 2.075 / 2.930 | 148.0 / 150.2 | N/A | N/A |
| Unit | 2 | 160 | 2.720 / 3.190 | 148.5 / 150.4 | N/A | N/A |
| Unit | 4 | 80 | 3.670 / 4.090 | 150.7 / 153.6 | N/A | N/A |
| PostgreSQL | 1 | 60 | 0.945 / 1.080 | 135.3 / 136.0 | Withdrawn | Withdrawn |
| PostgreSQL | 2 | 120 | 1.000 / 1.190 | 134.7 / 135.4 | Withdrawn | Withdrawn |
| PostgreSQL | 4 | 80 | 1.145 / 1.430 | 134.9 / 135.5 | Withdrawn | Withdrawn |

Time-aligned process samples in regression batches gave median aggregate owned
RSS peaks of 147.7/297.2/601.1 MiB for unit and 135.3/269.2/539.3 MiB for
PostgreSQL at concurrency 1/2/4. This uses the last sampled value until the next
sample or command end; it excludes containers, Ryuk and processes outside the
observable owned tree. It must not be read as a complete host budget.

There were 7,626 owned-container samples across baseline, repair preparation
and edited commands. All 617 observed PostgreSQL containers had the same
config image ID, `NanoCpus=1000000000` and `Memory=536870912`; sampled memory
limits also matched 512 MiB.

**Post-review telemetry correction:** the original observer read its phase
after a blocking stats request, so a baseline/preparation sample could be
misattributed to the edited command. Raw artifacts do not record the phase at
request start; retrospective validation of phase attribution is impossible.
The edited-container CPU/usage distributions, edited-sample counts, peak and
claim of edited-sample coverage for every valid trial are therefore withdrawn.
The observer now snapshots the phase before requesting stats and discards
samples that cross a phase transition, with a deterministic regression test.
These campaigns were not rerun. Image/limit inspection, total sample counts,
independently recorded process resources and edit-to-result latency remain
usable; no phase-specific container resource conclusion is made.

**Telemetry gaps:** 65 observer errors occurred in 60 integration trials:
48 missing `cpu_stats` responses, 14 HTTP 500 responses and three bounded API
request timeouts. The three API timeouts are telemetry timeouts, not test-command
timeouts. Edited-container sample coverage cannot be established retrospectively.
Campaign telemetry was flagged incomplete in 3/60 concurrency-1, 24/120
concurrency-2 and 33/80 concurrency-4 trials. These errors occurred during
baseline, preparation or edited observation; their cause was not proven.
They are observer/infrastructure incidents, not evidence of failing tests or
Piston contention. Resource columns remain partial observations, not a clean
no-contention resource proof.

**Cleanup:** all 278 integration attempts, including the two attempts that
started no container, confirmed an empty exact trial-label query after execution.
All 617 observed PostgreSQL containers were removed by normal Testcontainers/
Ryuk lifecycle; no explicit-ID fallback removal was needed. Final label-scoped
verification found no remaining measurement containers. No unrelated container
was removed or inspected for this report.

### Existing-Piston local reference

A separately pinned, non-container subset at the fixture repository revision ran
once after the campaigns: `Piston.Protocol.Tests.JsonRpcSerializerTests`, using
ordinary `dotnet test` with a fully-qualified-name filter. All 14 cases passed.
The first `--no-restore` invocation found missing assets; dependency restore was
setup before the passing run. Its reported test-stage duration was 180 ms, not an
edit-to-result sample, and is excluded from every campaign count/table above.
This verifies an existing repository subset, not the current-Piston backend.

## Limitations and blocked comparisons

The 409-case unit fixture is small synthetic domain code, not a large repository.
The integration fixture has five cases: idempotency, uniqueness, rollback,
concurrent updates and the distinct existing-failure sentinel. Both workloads
include incremental compilation, test-host startup and teardown; the integration
loop also creates a new PostgreSQL container per invocation. No build/test/
container-start stage attribution is available.

NuGet packages, filesystem caches, runtime/build-server state and the PostgreSQL
image were warm or shared, but trials rebuilt in separate fresh directories.
Restore and full baseline are setup, not edit latency or evidence of a persistent
warm service. No cold-cache/first-machine-start headline was calculated.

Concurrent workers are dependent observations. Worker median/p95 describe the
observed distribution; they are not independent replicate counts for uncertainty.
Batch means and slowest-worker summaries are reported separately. Concurrency
ratios are descriptive across different time windows on a shared host, not causal
estimates of pressure or scheduling benefit. Thermal state and continuous
host-wide pressure during execution were not observed.

Owned-process CPU is the maximum sampled sum of live descendants' cumulative
CPU, a lower bound, not full lifetime CPU. RSS is sampled and can miss brief or
reparented/reused MSBuild processes; it is not whole-host or Piston memory.
Container memory usage is a cgroup usage counter, not PostgreSQL process RSS.
The separate container observer polls at 250 ms plus API latency with bounded
three-second requests, adds unquantified overhead and can miss teardown peaks.
Telemetry errors remain explicit and are not treated as zero resource usage.

**Current-Piston comparator: blocked.** Existing MCP aggregate state does not
prove generation-bound, fresh per-test full-scope results for the exact edited
source. No adapter with that provenance was demonstrated, and the engine was not
changed to make a benchmark work. Paired backend improvement, confidence
intervals and G1 goals therefore remain uncomputed.

**Agent effort and blocked-wait metrics: null.** No real-agent sessions or
consented transcripts were authorized. Test-command duration is not agent
blocked time, and scripted preservation of a known failure does not establish a
reduction in diagnostic turns or cognitive effort.

Raw manifests, capacity checks, commands, sources, TRX, telemetry, events, trial
records, schedule/amendment and reduction are retained under the gitignored
`measurements/artifacts/`. Experiment directories use
`g0-{pilot|campaign}-{workload}-{scenario}-c{1|2|4}`; the three integration setup
smokes use `g0-integration-smoke`, `g0-integration-smoke-short` and
`g0-integration-smoke-ready`. This report publishes no hostnames, user paths,
container credentials or raw logs.

## G0 recommendation: proceed with narrowed scope

**Measured facts:** even these small fixtures spent a median 2.734 seconds
(unit) and 6.869 seconds (PostgreSQL) obtaining a full, fresh regression result
at concurrency 1. At concurrency 4, those medians were 6.214 and 13.814 seconds,
with p95s of 6.554 and 17.303 seconds. Execution correctness and label-scoped
cleanup held, sampled memory was modest, and container telemetry had explicit
gaps. No agent effort or backend advantage was measured.

**Inference and decision:** repeated multi-second loops and approximately
doubled per-workspace latency at four simultaneous worktrees are meaningful
enough to justify a small proof-of-value service experiment, especially for
container-backed verification. They do not justify the entire proposed hub,
watcher/prewarming, history and coverage architecture, nor establish a need for
aggressive resource scheduling on this well-provisioned host.

Proceed first with generation-bound full-scope result provenance, explicit
execution/ownership and a bounded resource-budget comparison. Require an
equivalent dotnet-versus-service experiment, a telemetry-overhead check and
representative larger repositories before claiming faster verification or
expanding architecture. Agent waiting/known-failure effort requires separately
authorized real-agent evidence. G1 targets remain unproven; this is a
scope-limited G0 recommendation, not a claim that Piston is faster.
