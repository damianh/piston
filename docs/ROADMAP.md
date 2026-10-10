# Piston: test backend for coding agents

## Direction

Piston is moving toward a session-aware .NET test backend for coding agents.
Its value should come from remembering results, coordinating execution across
worktrees, and showing which changed code tests exercised -- not simply wrapping
`dotnet test`.

Humans inspect the same evidence through a Copilot app canvas or browser UI.
Other harnesses connect through MCP and lightweight integration hooks. Aspire is
one integration-test use case, alongside Testcontainers, WebApplicationFactory,
and Playwright; it does not determine the architecture.

This document describes **proposed work**, not currently available functionality.
README and ARCHITECTURE describe the implementation. Later phases are conditional
on evidence from the measurement gates.

## Product boundaries

- **Memory:** distinguish newly observed failures from baseline failures and
  suspected flakiness, with persistent run history.
- **Coordination:** prioritize requested verification over speculative runs and
  background work; share CPU, memory, and integration-test resource budgets.
- **Change coverage:** relate changed executable lines to individual tests,
  outcomes, and the source version actually exercised.
- **Visualization:** session timelines, test detail, workspace activity, and a
  coverage-aware diff, available in a harness canvas and browser.

Piston does not assign tasks to agents or coordinate their conversations. It is
not a replacement for an IDE's Roslyn tools or an NCrunch-style IDE extension.
Test selection is an optimization, not the primary differentiator.

## Proposed architecture

Use a small **per-user hub** with isolated **per-worktree worker processes**:

- The hub owns workspace/session registration, scheduling, history, and the UI.
  It does not load MSBuild.
- Workers own solution evaluation, file watching, builds, and test execution.
  Isolation accommodates different SDKs and contains evaluation failures.
- A stdio MCP shim connects each harness client to the hub; hooks supply explicit
  session identity where available. MCP roots or an explicit workspace take
  precedence over a working-directory fallback.
- Store history outside checkouts, under a per-user Piston directory, grouped by
  repository identity. Worktree deletion must not delete shared history.
- Reuse the browser visualization in a thin Copilot canvas extension rather than
  building a separate UI for each harness.

Worker SDK selection needs explicit validation against `global.json`, supported
MSBuild versions, and runtime compatibility. Runtime roll-forward alone does not
choose the correct MSBuild installation.

### Lifecycle and ownership

Use leases from live connections or renewable registrations, not session-end
hooks alone. Hooks can be missed when a harness is killed.

- Start the hub on demand, guarded against simultaneous startup.
- Start a worker when a workspace gains an execution lease.
- Release watchers and process trees when the last execution lease expires.
  Keep durable results accessible without continuing to run tests.
- Separate workspace leases, session identities, and run subscriptions. A
  long-lived IDE connection is not necessarily one agent session.
- A shared run must survive until its remaining subscribers no longer need it;
  cancelling one session must not cancel another session's verification.
- Recover connections after a hub restart; mark interrupted runs explicitly.
- Ensure worker and test descendants cannot outlive shutdown indefinitely or
  block worktree removal. Validate this on Windows as well as Unix.

Idle limits, heartbeat expiry, restart draining, and explicit shutdown behavior
will be chosen and tested during Phase 1, rather than treated as existing
guarantees. Baseline work is independently owned, but still consumes scheduled
resources and must not keep an otherwise idle service alive indefinitely.

### Distribution

Prefer a **.NET tool on NuGet**, provisionally named `Piston`. A package lookup is
not a reservation or confirmation of ownership.

- Support an installed `piston` command and, where supported, `dnx` invocation.
- Start hub and workers on demand: no installer or privileged OS service.
- Keep role executables and contracts version-compatible; validate upgrade and
  file-lock behavior before relying on side-by-side launch paths.
- Ship harness integration assets with the tool. Installation should preserve
  user configuration and make changes explicit.
- Freeze desktop development and replace desktop-installer release automation
  with tool packaging. Retain the desktop source until a later decision.

The tool requires an appropriate .NET runtime/SDK installation. Building a target
repository may require additional SDK versions pinned by that repository.
MCP registry/package metadata and `dnx` command syntax must be validated before
documenting them as supported installation paths.

## Phase 0: correctness, simplification, and baseline

1. **Build correctness:** rebuild affected test projects and dependencies before
   running tests with `--no-build`. Avoid whole-solution builds for test-only
   edits. Preserve solution formats, runner routing, and coverage behavior.
   Add a real regression proving that changing a referenced library changes the
   executed test result. Prefer one filtered build where SDK support permits it.
2. **Remove obsolete surfaces:** remove the Roslyn worker, Roslyn MCP tools,
   diagnostic polling/UI, and VS Code extension. Clean their references and
   dependencies. Capture relevant supervision lessons before deleting code.
3. **Tool distribution:** create a working tool package and replace the
   Velopack-based installer release workflow. Validate isolated installation and
   CLI execution. Actual NuGet publication and package-name reservation are
   separate release actions, not implicit implementation steps.
4. **Documentation:** distinguish implemented behavior from future proposals,
   especially persistence, coverage attribution, prewarming, and build strategy.
5. **Measurement:** propose repeatable unit-heavy and container-backed workloads.
   Measure edit-to-result latency, agent test-wait time, effort spent on existing
   failures, contention, and resource use. Distinguish synthetic concurrency
   tests from real multi-agent sessions.

**Status:** items 1–5 landed in #7 (build correctness), #4 (removal and tool
packaging), #5 (documentation) and #6 (measurement harness and methodology).
No package has been published. The harness has passed its self-tests and
unit-fixture smoke runs only; container-backed runs, repeated campaigns and
real-agent sessions still need explicit approval, so no G0 evidence exists yet.

**Gate G0:** decide whether these costs are meaningful enough to justify a service.
Creating a benchmark harness is not equivalent to collecting evidence or passing
the gate. Repository selection, real-agent execution, and experiment permissions
must be agreed before running the measurement workload.

## Phase 1: hub, workers, identity, and memory

- Split the existing daemon into worker execution and hub responsibilities.
- Implement registration, leases, idle cleanup, supervised process shutdown, and
  protocol-version negotiation.
- Add scheduling: requested verification, speculative file-change runs, baseline
  work, then coverage backfill. Coalesce work and avoid starvation.
- Persist workspace/session identity, source fingerprints, build context, runs,
  test identities, and outcomes.
- Establish a declared baseline, typically the merge-base with a configured base
  ref, in a Piston-managed worktree.
- Explore MTP server mode for structured results and stable test identities.
  Keep VSTest/TRX as a compatibility path.
- Classify failures as new relative to known baseline evidence, pre-existing,
  suspected flaky, or unknown. Missing baseline evidence is not proof a failure
  is new.

Source content alone cannot prove freshness or flakiness. Include SDK/runtime,
target framework, build configuration, relevant inputs/environment, and selected
tests; reject or mark results stale if inputs change during execution. Identical
source with different outcomes is evidence of nondeterminism, not a diagnosis.

**Exit:** two worktrees share scheduling and history while execution remains
isolated; ownership, cleanup, interruption, and baseline behavior have tests.

## Phase 2: agent API and harness integrations

- Add `piston mcp`, a stdio bridge that discovers/starts the hub and resolves the
  workspace without loading MSBuild in the client.
- Provide concise verification, status, test-detail, explicit-run, and history
  operations. Report selection scope, freshness, incomplete runs, and failures
  before verbose detail. Selective verification is not full-suite verification.
- Normalize session and edit events through harness adapters.
- Ship a Copilot extension for registration, guidance, and the canvas; add Claude
  Code integration and documentation for generic MCP clients.
- Avoid duplicate triggering from hooks plus file watching. Do not intercept
  arbitrary test commands or modify harness settings without explicit opt-in.

**Gate G1:** compare against plain `dotnet test` with equivalent workloads.
Proposed goals are 30% lower verified-result latency, 50% less agent effort on
pre-existing failures where present, and no observed resource-contention failures.
Record raw results and limitations; these goals do not override correctness.

## Phase 3: canvas and browser visualization

- Session timeline: edits, runs, outcomes, freshness, and failure classification.
- Workspace overview: active sessions, queued/running work, and resource use.
- Repository health: baseline evidence and repeated-outcome history.
- Test detail: errors, captured output, history, and relevant artifacts.
- Serve the same UI on loopback for the browser and a Copilot canvas.
- Reuse the existing web UI where practical; only replace its technology if
  measured behavior warrants it.

Validate canvas reload/resume, disconnected state, actions, accessibility,
theme integration, and ownership of durable state. Read-only inspection should
not silently activate continuous test execution.

## Phase 4: fine-grained coverage

### Collection spike

Verify available collectors and APIs against reproducible fixtures rather than
assuming standard reports contain per-test attribution.

1. Prefer existing coverage tooling, investigating `dotnet-coverage` snapshots
   and reset between serial test requests, with dynamic or static instrumentation
   according to platform support.
2. Use isolated per-test runs as a correctness reference. Per-class runs are
   explicitly coarser and must not be labeled per-test.
3. Consider Piston-owned instrumentation only if existing approaches cannot meet
   correctness and overhead requirements.

Measure attribution bleed, async/background work, fixtures, parameterized tests,
runner identity, platform support, and overhead. Proposed backfill goal: at most
twice the comparable workload cost, with no observed attribution bleed in the
serial validation fixtures. Cross-process service coverage is not an initial
acceptance requirement.

### Implementation

- Persist genuine per-test executable-line and, where available, branch hits,
  tied to test identities and source/build versions.
- Store aggregate coverage separately; never assign an aggregate report to every
  test as if it demonstrated individual attribution.
- Run detailed coverage as scheduled backfill without blocking ordinary
  verification. Display pending/unknown/stale explicitly.
- Show changed executable lines as covered by passing tests, covered only by
  failing tests, uncovered, non-executable, or unknown/stale.
- Add a coverage-aware diff to the canvas with drill-down to covering tests.
- Investigate method-level identity to handle line shifts, but do not reuse old
  line evidence after edits without validated remapping.
- Use trustworthy maps for selective execution with conservative fallback.
  Track upstream affected-test selection work before building competing
  infrastructure; availability and compatibility must be checked at integration.

## Phase 5: integration-test resources

Build only the resource constraints demonstrated by G0/G1.

- Infer likely resource needs from package references, with explicit overrides;
  package presence alone is not proof every test needs exclusive resources.
- Schedule container, memory, or explicitly named exclusive-resource budgets.
- Address demonstrated fixed-port/container-name conflicts conservatively.
- Surface per-test output and relevant artifacts in the API and canvas.

Aspire-specific telemetry, out-of-process coverage, and warm AppHost reuse remain
optional future work, not dependencies of the core product.

## Deferred work and risks

- PR verdict/change-coverage reporting; remote/cloud-host transport and isolation.
- Desktop revival; deeper VSTest per-test attribution.
- Build-output contention with commands outside Piston's scheduler.
- Attribution ambiguity when several sessions edit the same worktree.
- SDK compatibility and harness hook/API changes.
- Upgrade/file-lock behavior and complete descendant process cleanup.
- Cost of baseline and coverage backfill; honest stale/partial evidence.

Later phases are not authorized merely by landing the roadmap.
