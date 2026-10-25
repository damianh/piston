# Samples

`Samples.slnx` stands in for a user's solution in the framework smoke tests (`dotnet run build.cs -- smoke`, run by
CI). It contains one test project per framework, all on Microsoft.Testing.Platform:

| Project | Framework | How it opts into MTP |
| --- | --- | --- |
| `MSTestSample` | MSTest | `EnableMSTestRunner` |
| `NUnitSample` | NUnit | `EnableNUnitRunner` |
| `XUnitV3Sample` | xUnit v3 | the `xunit.v3.mtp-v2` package |
| `TUnitSample` | TUnit | the `TUnit` package |

Each has three tests, `<Framework>Passes`, `<Framework>Fails` (deliberately) and `<Framework>Skipped`, so the smoke
tests expect 12 tests: 4 passed, 4 failed and 4 skipped, from both `piston daemon` and `verify` over `piston mcp`. The
failures are intentional, so the samples are not part of `Piston.slnx` and are not run by `dotnet test`.

The test names differ per framework because MSTest, NUnit and TUnit report bare method names as test IDs over the
Microsoft.Testing.Platform server protocol. Names repeated across projects would show up as duplicate test IDs, and
`verify` would report `Incomplete`.

`.editorconfig` here sets `root = true`, so builds do not read the repository's `.editorconfig`, which lies outside
the samples' source fingerprint (`verify` would report `NotFullScope`).

The samples have their own `Directory.Build.props` and `Directory.Packages.props`, so the repository's build
settings (warnings as errors, preview language version, package versions) do not apply to them.

Editing a file here makes a Piston daemon watching `Piston.slnx` start a full run, since no project in that solution
owns the file.
