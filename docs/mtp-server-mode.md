# MTP server-mode investigation

## Decision

**Keep MTP server mode as Piston's primary MTP execution path, and keep VSTest/TRX as the compatibility path.**
The protocol gives Piston structured per-test updates and supports discovery followed by selected test-node
execution. Captures from MSTest, NUnit, TUnit and xUnit v3 show stable node UIDs across discovery, cold runs and
warm-host reruns of the same build. However, stability across source changes has not been established, and Piston
currently discards the UID when it constructs a `TestResult`.

Coverage runs now use server mode with support for multiple controller connections, retaining `dotnet test` as
the fallback. In the original probe, a coverage-enabled MSTest app connected to the MTP
server client but did not return test updates or a request response within two minutes; no coverage report was
produced. The existing `dotnet test` path completed the same nine-test probe in under three seconds and produced
one Cobertura report. This confirms the current fallback is valuable, but does not establish whether the server
mode limitation is in MTP, its coverage extension, or Piston's protocol lifecycle. The follow-up for
[#36](https://github.com/damianh/piston/issues/36) identified the missing controller connection handling in Piston.

## Method

The isolated solution at [`tests/fixtures/mtp-probe`](../tests/fixtures/mtp-probe) targets .NET 10 and uses
MSTest 4.5.1, NUnit 5.0.0, TUnit 1.76.0 and xunit.v3.mtp-v2 4.0.2. Each project contains the same-named
`SameName` method in two classes, two parameterized cases, an assertion failure, a thrown
exception, console output and a skipped test with a reason. Each also assigns a category/trait. The MSTest project
references `Microsoft.Testing.Extensions.CodeCoverage` 18.12.0. MSTest, NUnit and xUnit also set a custom display
name; TUnit's `CustomName(1)` uses its default parameterized display name.

`MtpNodeCaptureTests` is opt-in: set `PISTON_CAPTURE_MTP_NODES=1` when running the test to build and execute the
probe. It stores normalized discovery, run, cold-rerun, warm-run and warm-rerun node arrays in
[`tests/Piston.Engine.Tests/Services/MtpNodes`](../tests/Piston.Engine.Tests/Services/MtpNodes). Timing fields
and the probe's absolute path are normalized. `MtpRecordedNodeTests` runs against those committed captures and
records the current mapping behavior.

This is a characterization, not a compatibility guarantee: it covers one Linux environment and these package
versions. It does not test UID stability after editing/reordering test methods, adding tests, changing package
versions, or rebuilding from changed source. It does not exercise test attachments or inspect MTP initialize
capabilities.

## Findings

| Framework | `display-name` for `SameName` | UID shape in capture | Same UIDs across cold/warm reruns? | `location.file` and line? |
|---|---|---|---|---|
| MSTest | `SameName` | Opaque GUID | Yes | Yes |
| NUnit | `SameName` | Fully qualified test/case name | Yes | No |
| TUnit | `SameName` | Class, method and case data | Yes | Yes |
| xUnit v3 | Fully qualified display name | Opaque SHA-256-looking value | Yes | Yes |

All four adapters reported `location.type` and `location.method`. Parameterized cases had distinct UIDs and
display names, but shared the same method location; Piston must not use `location.type + location.method` alone
as a result key for data-driven cases. The capture tests compare each UID together with its type, method and
display name across discovery, cold runs and warm-host reruns of this unchanged build only.

The current `ToTestResult` mapping exposes status, duration, console output (when the adapter reports it), error
message, stack trace, method and app path. Only MSTest populated the failed assertion's separate `assert.expected`
and `assert.actual` fields (`"2"` and `"1"`); NUnit, TUnit and xUnit emitted empty strings and put the values in
`error.message`. Piston appends populated assertion fields to the error-message string instead of preserving them
as structured values. The characterization checks these per-framework values, including the empty strings; the
presence of the keys alone does not establish usable assertion metadata. Skip reasons arrive in `error.message`.
Thrown exceptions have error text and a stack trace but no separate
exception-type property in these nodes. MTP also reports source file/line and traits, which this mapping drops.
Output capture differed: MSTest, NUnit and TUnit reported `standardOutput` for the probe; xUnit v3 did not.

For all four frameworks, Piston's current MTP mapping uses `display-name` as `FullyQualifiedName`. In this probe,
that produces duplicate `ResultKey`s for `SameName` in two classes under MSTest, NUnit and TUnit; xUnit's
fully-qualified display names avoid that particular collision. MTP node UIDs distinguish the two tests in every
capture. This reproduces the issue in [#42](https://github.com/damianh/piston/issues/42).

### Coverage and progress

The observations below describe the original single-connection client. With `--coverage`, the controller
connects first and advertises `capabilities.testing.multipleConnectionProvider`; the relaunched test host
connects separately and serves requests. Piston now initializes both connections, sends requests to the active
test host, and drains the passive controller connection until shutdown. Coverage runs use a fresh host and
allow the controller time to finish its report after the child exits. Integration tests cover report generation
and server-mode progress; the original stalled capture is retained as investigation evidence.

The coverage experiment launched the MSTest app with `--server`, the loopback client options and the same
`--coverage --coverage-output-format cobertura --results-directory` arguments used by Piston. It connected to the
client, but the run request produced no test updates or response within 120 seconds and wrote no Cobertura report.
The process printed the server connection message twice. The capture is in
[`coverage-server-mode.json`](../tests/Piston.Engine.Tests/Services/MtpNodes/coverage-server-mode.json).

The comparison via `dotnet test --project ... --configuration Release --no-build --output Detailed --no-ansi
--coverage ...` ran all nine tests (six passed, two deliberately failed, one skipped), exited with the expected
test-failure code, and emitted one Cobertura report. That confirms coverage collection itself works through the
existing fallback. It does not provide MTP server-mode per-test progress; the current runner's stdout parser can
publish completed tests, not an in-progress test. See [#36](https://github.com/damianh/piston/issues/36).

## Follow-up

- [#42](https://github.com/damianh/piston/issues/42): use an identity that includes the test type and a
  parameter/case discriminator; keep display names for display. Decide how identity changes affect stored history
  and downstream result keys before changing the key.
- [#36](https://github.com/damianh/piston/issues/36): the controller-connection fix enables coverage in server
  mode while retaining the `dotnet test` fallback.
- Preserve assertion values, skip reason, exception details, source location and traits as structured test-result
  fields. This is not covered by #36 or #42; see [#60](https://github.com/damianh/piston/issues/60).
- Attachments and related artifacts belong with the scope of [#54](https://github.com/damianh/piston/issues/54).

VSTest/TRX remains the compatibility path for projects that are not MTP projects or when the MTP server path
cannot be used. This investigation made no production behavior changes.
