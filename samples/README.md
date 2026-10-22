# Samples

`Samples.slnx` stands in for a user's solution in the framework smoke test (`eng/smoke-test.mjs`, run by CI). It
contains one test project per framework, all on Microsoft.Testing.Platform:

| Project | Framework | How it opts into MTP |
| --- | --- | --- |
| `MSTestSample` | MSTest | `EnableMSTestRunner` |
| `NUnitSample` | NUnit | `EnableNUnitRunner` |
| `XUnitV3Sample` | xUnit v3 | the `xunit.v3.mtp-v2` package |
| `TUnitSample` | TUnit | the `TUnit` package |

Each has three tests: `Passes`, `Fails` (deliberately) and `Skipped`, so the smoke test expects 12 tests: 4 passed,
4 failed and 4 skipped. The failures are intentional, so the samples are not part of `Piston.slnx` and are not run
by `dotnet test`.

The samples have their own `Directory.Build.props` and `Directory.Packages.props`, so the repository's build
settings (warnings as errors, preview language version, package versions) do not apply to them.

Editing a file here makes a Piston daemon watching `Piston.slnx` start a full run, since no project in that solution
owns the file.
