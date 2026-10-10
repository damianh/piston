# Harness validation

## Approved local G0 campaign (2026-10-10)

The approved local measurement evidence is in [results/g0-local.md](results/g0-local.md).
Its recommendation is **proceed with narrowed scope**, not a backend speedup
claim or a G1 result.

Executed on Linux with .NET SDK 10.0.401 and Python 3.14.7:

- Seventeen Python self-tests passed, including tests for local-socket
  guards, exact-label container observation/removal, mismatched-label and
  surviving-container cleanup refusal, explicit telemetry errors, phase-crossing
  sample rejection and a DELETE-404 cleanup race.
- Eight five-batch pilots, then eighteen twenty-batch single-condition campaigns.
  Regression ran for both workloads at concurrency 1, 2 and 4. Repair and
  pre-existing-failure scenarios ran at concurrency 1 and 2; shared rounding and
  compile diagnostics also ran at concurrency 1 and 2 for the unit fixture.
  The initial larger schedule was narrowed after the user challenged its elapsed
  time; unexecuted concurrency-4 scenarios are explicitly absent.
- 683 attempted fixture trials: 681 valid, two invalid integration setup
  smokes caused by socket configuration, zero test-command timeouts. Valid
  samples comprise 616 full-scope identity/status oracles and 65 compile
  diagnostics oracles. All 640 statistical campaign trials were correct.
- PostgreSQL 18.6 executed using immutable digest
  `sha256:885953109528ad3dfc90362b1a6f50a78620b5315be19f187753d267e484dc5b`
  through rootless Podman 5.8.7. All 617 observed PostgreSQL containers retained
  1 CPU / 512 MiB limits. All 278 integration attempts confirmed label-scoped
  cleanup; no fallback removal or unrelated-container cleanup occurred.
- Container telemetry retained 65 errors across 60 trials (missing counters,
  HTTP 500 and three telemetry API timeouts). These are not test-command
  timeouts, are not silently discarded, and limit resource/contention claims.
  Review identified a phase-attribution race in the original observer.
  Phase-specific container resource figures and edited-sample coverage claims
  are withdrawn because raw records lack request-start phases. The observer is
  fixed, but campaigns were not rerun; process resources and latency are unaffected.
- One existing-Piston non-container reference subset, the 14 serializer tests,
  passed after restoring initially missing assets. No engine test or source was
  modified, and this is not a backend comparator.

Current-Piston A/B remains blocked by generation-bound full-scope provenance.
Agent metrics remain null because no real-agent sessions/transcripts were
authorized. Raw evidence remains gitignored. The report separates worker and
batch distributions, setup, telemetry gaps, measured facts and recommendation.
Runner JSON summaries do not automatically decide G0 or calculate backend benefit.

## Initial smoke validation (historical)

This section records implementation smoke validation, **not a performance
campaign**. G0 was pending at that point. No baseline-versus-backend gate result
was calculated.

Environment: Linux, .NET SDK 10.0.401, Python 3.14.7. Dependency restores use
committed lock files and warnings-as-errors; the PostgreSQL fixture uses
Testcontainers.PostgreSql 4.16.0 and Npgsql 9.0.4.

Executed:

- Eleven Python self-tests covering manifest containment, missing/stale/duplicate/
  incomplete TRX, scope/status/exit oracle mismatches, unique sentinels, overlapping
  blocked waits, command timeout/nonzero exit, paired batch reduction, null benefit
  semantics, container/campaign permission guards and concurrent workspace/barrier
  isolation with a mocked command (no actual concurrent workload launched).
- One sequential unit smoke per scenario: baseline, regression, repair, rounding,
  known-failure, regression-with-existing-failure and compile-error. Each full test
  run had 409 identities; all scenario-specific status/exit oracles agreed.
  Compile-error verified CS0103 and no test execution. The combined scenario
  preserved the known failure and identified both new discount failures.
- Locked integration restore and compilation with zero warnings/errors, without
  starting Docker or containers.

Not executed:

- PostgreSQL runtime smoke: separate Docker/image/resource approval is absent.
  Its digest remains an operator-approved prerequisite rather than a mutable tag.
- Concurrent/repeated statistical campaigns, external repository workloads and
  real paid multi-agent sessions: not authorized by initial harness approval.
- Current-Piston A/B: generation-bound, full-scope result capture is not established
  by its existing MCP aggregate interface. The comparator is explicitly blocked.

Raw local smoke artifacts are gitignored and retained for diagnosis. Timing
values from these single-condition smoke runs are not evidence of backend benefit.
The Python paired-reduction tests use synthetic inputs, not measurement results.
