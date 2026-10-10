# Comparison protocol and output contract

## Questions, controls and gates

Test whether an agent-facing backend benefits ordinary .NET edits compared with
plain `dotnet test`, before building per-user hubs, per-worktree workers,
history/baselines, shared scheduling or genuine per-test change coverage.
Correctness always overrides performance. G0 needs repeated baseline cost
measurements to decide whether a service investigation is justified; it does not
require or imply a backend improvement. See [the local G0 evidence](results/g0-local.md).
Later proposed G1 goals are 30% lower edit-to-correct-result
latency, 50% fewer pre-existing-failure turns where present and zero **observed**
contention failures. Goals are not measured outcomes; zero observed incidents
cannot prove impossibility and zero baseline turns have no relative reduction.

Use both unit-heavy and container-backed workloads, not just Aspire. Local
fixtures are initial repeatability controls. Separately record a pinned
non-container subset of existing Piston tests as a local reference, without
editing engine source/tests. External follow-ups, after repository/clone consent:
VerifyTests/Verify for a mature unit-heavy library and a representative
Testcontainers/PostgreSQL service or official example. Verify license, pinned
revision, SDK, isolation and failure incidence before adopting them. These are
candidate repositories, not validated fixtures or results.

Every condition must use identical source revisions/edits and full test scope,
test parallelism, package/image pins and host allocation. Ordinary dotnet agents
may establish their own baseline; do not handicap them. Restore and image pulls
are setup, recorded separately. Report startup and warm incremental loops
separately. Do not substitute filtered/impacted tests for full scope in headline
comparisons; any later selective run needs a separate full-suite oracle and must
not equate aggregate coverage with genuine per-test change coverage.

Pilot five paired repetitions per scenario at concurrency 1; after checking
feasibility and permission use twenty pairs per approved scenario/concurrency.
Include localized regression/repair, shared dependency change and compile-error
diagnostics. Concurrent batches use independent workspaces and an edit barrier;
use concurrency 2 first, 4 only after capacity approval. Counterbalance condition
order with a recorded random seed, match cache state, and keep batches as units
of analysis. The current runner executes single-condition baseline campaigns, not a
randomized two-condition comparison.

Pair on workload, scenario, repetition and concurrency. Compute paired absolute
and relative differences, median/p95 per condition, and batch-resampled
uncertainty intervals with the recorded seed. Publish all attempted, invalid,
timeout and task-failure counts and reasons. Do not silently discard slower runs.
Correctness disagreements are reported and disqualify benefit calculations.
Do not pool first-run/warm results, compile-error diagnostics with test results,
or dependent concurrent processes as independent observations.

Freshness requires the exact edit hash, no source change during execution, new
result artifacts, patch-sensitive sentinel statuses and the entire expected test
identity/status set. Skips must agree. Existing Piston's MCP aggregate state does
not prove this; current-Piston comparison is blocked until a bounded adapter
demonstrates it with existing interfaces. Do not change the engine to rescue a
benchmark. A static known-failure report is information assistance, not evidence
of an implemented history backend.

## Schema version 1

The executable runner's JSON output is the baseline subset below; future adapters
must add explicit fields rather than silently filling unsupported values:

| Record | Required evidence |
|---|---|
| Manifest | Experiment ID, schema version, fixture hash/repository revision, scenario/patch, commands, SDK/runtime/OS, scope/discovery hash, dependency locks, image digest, concurrency, cache/setup policy, timeout, permissions |
| Event JSONL | Experiment/trial/batch/session IDs, source (`scripted` or `agent`), event type, UTC and monotonic elapsed ns, resource/command identity, evidence/error detail |
| Trial JSONL | Condition/workload/scenario, base/pre-edit/edit hashes, exact expected/observed identities/statuses or diagnostics oracle, correctness, timeout/exit, edit-to-correct-result ms, setup durations, sampled resources and incident evidence |
| Agent additions | Model/provider/settings, prompt hash, consented transcript reference, total turns, annotated pre-existing-failure turns, explicit blocked-wait intervals and their union |
| Summary | Attempted/valid/invalid/timeouts and reasons, batch counts, median/p95, paired changes and uncertainty only when comparable evidence exists, resource observations, incidents, correctness failures, G0 and rationale |

Unsupported agent metrics are **null**, not zero. Test-tool duration is not
blocked agent time. Unsupported build/test stage durations are null with limitations. Container
statistics are collected for approved local integration trials; unit trials have
none, and missing observations/errors remain explicit. Compile-error diagnostics are a separate correctness
oracle; never invent per-test passing counts for compilation failures.

Classify incidents only with evidence: port/resource collision, process/container
start failure, timeout, or pressure-correlated degradation. Distinguish assertion,
setup, infrastructure and product failures; unclassified is explicit. Capture
owned-container stats and cleanup evidence before a container campaign claims no
contention. Record cgroup CPU/memory constraints and host pressure (context only),
thermal/cache state where observable, telemetry cadence/overhead and polling delay.
Never inspect unrelated users' processes or apply host-wide stress.

## Boundaries and permissions

Initial approval covers scripts/fixtures/focused docs, runner self-tests and unit
smoke, plus integration compilation without starting containers. Docker lifecycle,
image downloads, resource limits and disk capacity need separate explicit
approval. External clones, full campaigns, paid real-agent sessions and cost
ceilings also require subsequent approval. No dynamic workflow is used.

The initial validated PR may contain blocked integration execution and comparator
conditions if clearly labeled. Its baseline instrumentation is not a G0 decision.
Local synthetic domain code is not a population-wide representative repository;
repeatability must be followed by external validation. Shared hosts, runtime
variance, adapter behavior, cache state, image pulls and short-lived resource
sampling all limit interpretation.
