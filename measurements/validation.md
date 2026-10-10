# Initial harness validation

This records implementation smoke validation, **not a performance campaign**.
G0 remains pending. No baseline-versus-backend gate result was calculated.

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
