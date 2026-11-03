// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Piston.Engine.Coverage;
using Piston.Engine.Impact;
using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Orchestration;

public sealed class OrchestratorBackfillTests : IDisposable
{
    private const string Project = "/src/A.Tests/A.Tests.csproj";

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("piston-backfill-").FullName;
    private readonly PistonState _state = new();
    private readonly SqliteCoverageStore _store = new();
    private readonly ClassRunner _runner = new();
    private readonly RecordingProcessor _processor = new();
    private readonly ProvenanceImpactAnalyzer _analyzer = new([Project]);
    private readonly PistonOrchestrator _orchestrator;

    public OrchestratorBackfillTests()
    {
        File.WriteAllText(Path.Combine(_root, "App.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Code { }");
        _orchestrator = new PistonOrchestrator(
            new StubFileWatcherService(), new ProvenanceBuildService(), _runner, _analyzer,
            _state, _store, _processor, coverageEnabled: true)
        {
            BackfillGranularity = ImpactGranularity.Class,
            BackfillDelay = TimeSpan.Zero,
        };
    }

    public void Dispose()
    {
        _orchestrator.Dispose();
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private async Task StartAsync()
    {
        var previousBackfill = _orchestrator.BackfillTask;
        await _orchestrator.StartAsync(Path.Combine(_root, "App.slnx"));
        await WaitForAsync(() => _state.LastRun is { Trigger: TestRunTrigger.Startup } run
            && _state.ProjectCoverage.TryGetValue(Project, out var coverage) && coverage.RunId == run.RunId
            && !_state.IsPublishingResults);
        if (_orchestrator.BackfillGranularity is not null && _state.CoverageError is null)
        {
            await WaitForAsync(() => !ReferenceEquals(previousBackfill, _orchestrator.BackfillTask));
        }
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met.");
            }

            await Task.Delay(10, _ct);
        }
    }

    [Fact]
    public async Task WhenIdle_MeasuresEachClassAloneAndRecordsClassUnits()
    {
        await StartAsync();
        await WaitForAsync(() => _processor.Recorded.Count == 2);
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.Equal([@"FullyQualifiedName~A.Tests.Bar.", @"FullyQualifiedName~A.Tests.Foo."], _runner.Filters.Where(f => f is not null));
        Assert.All(_processor.Recorded, r => Assert.Equal(ImpactGranularity.Class, r.Granularity));
        var units = _processor.Recorded.SelectMany(r => r.Units).ToList();
        Assert.Equal(["A.Tests.Bar", "A.Tests.Foo"], units.Select(u => u.UnitKey));
        Assert.All(units, u => Assert.False(u.Merge));
        Assert.Equal(["A.Tests.Foo.One", "A.Tests.Foo.Two"], units[1].TestFqns);
        Assert.Null(_state.ImpactBackfill);

        // Measured units are current, so the next idle period measures nothing.
        _ = await _orchestrator.RequestRunAsync();
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(10), _ct);
        Assert.Equal(2, _processor.Recorded.Count);
    }

    [Fact]
    public async Task FrameworkChangeInvalidatesFineUnitsBeforeBackfillCanRemeasureThem()
    {
        _runner.TargetFramework = "net8.0";
        await StartAsync();
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(10), _ct);
        var originalUnits = _store.GetUnits(Project, ImpactGranularity.Class);
        Assert.Equal(2, originalUnits.Count);
        Assert.All(originalUnits, unit => Assert.All(unit.TestResultKeys!,
            key => Assert.EndsWith(" [net8.0]", key, StringComparison.Ordinal)));

        _analyzer.TargetFrameworkCount = 2;
        _runner.MultiTargetTests = true;
        _runner.MultiTargetForegroundReports = true;
        _runner.BlockFiltered = true;
        var unitsDuringCoverageProcessing = -1;
        _processor.OnProcess = () => unitsDuringCoverageProcessing = _store.GetUnits(Project, ImpactGranularity.Class).Count;
        var previousBackfill = _orchestrator.BackfillTask;

        var run = await _orchestrator.RequestRunAsync().WaitAsync(TimeSpan.FromSeconds(10), _ct);
        await WaitForAsync(() => !ReferenceEquals(previousBackfill, _orchestrator.BackfillTask));
        await _runner.FilteredStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.Equal(TestRunStatus.Completed, run!.Status);
        Assert.Equal(CoverageCollectionStatus.Measured, _state.ProjectCoverage[Project].Status);
        Assert.Equal(0, unitsDuringCoverageProcessing);
        Assert.Empty(_store.GetUnits(Project, ImpactGranularity.Class));
        Assert.Equal(2, _processor.Recorded.Count);
    }

    [Fact]
    public async Task ANewRunCancelsTheBackfill()
    {
        _runner.BlockFiltered = true;
        await StartAsync();
        await _runner.FilteredStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
        Assert.NotNull(_state.ImpactBackfill);

        var run = await _orchestrator.RequestRunAsync().WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.Equal(TestRunStatus.Completed, run!.Status);
        Assert.True(_runner.FilteredCancelled);
        Assert.Empty(_processor.Recorded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackfillResourceWait_IsPublishedAndCleared_OnAcquisitionOrCancellation(bool cancel)
    {
        _runner.ReportResourceWaits = true;
        _runner.BlockFiltered = cancel;
        var observed = new ConcurrentQueue<(ProjectRunStatus? Status, bool Waiting)>();
        _state.StateChanged += () =>
        {
            if (_state.ImpactBackfill is not null)
            {
                observed.Enqueue((
                    _state.ProjectStatuses.TryGetValue(Project, out var status) ? status : null,
                    _state.ProjectResourceWaits.ContainsKey(Project)));
            }
        };
        await StartAsync();
        if (cancel)
        {
            await _runner.FilteredStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
            Assert.True(_state.ProjectResourceWaits.ContainsKey(Project));
            _orchestrator.BackfillGranularity = null;
            _ = await _orchestrator.RequestRunAsync().WaitAsync(TimeSpan.FromSeconds(10), _ct);
            Assert.True(_runner.FilteredCancelled);
        }
        else
        {
            await WaitForAsync(() => _processor.Recorded.Count == 2);
        }

        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(10), _ct);
        Assert.Contains((ProjectRunStatus.WaitingForResources, true), observed);
        if (!cancel)
        {
            Assert.Contains((ProjectRunStatus.Pending, false), observed);
            Assert.Contains((ProjectRunStatus.Running, false), observed);
        }
        Assert.Empty(_state.ProjectResourceWaits);
        Assert.Equal(ProjectRunStatus.Completed, _state.ProjectStatuses[Project]);
    }

    [Fact]
    public async Task IsOffWithoutAGranularity()
    {
        _orchestrator.BackfillGranularity = null;
        await StartAsync();
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.All(_runner.Filters, f => Assert.Null(f));
    }

    [Theory]
    [InlineData("test", 3)]
    [InlineData("class", 2)]
    [InlineData("off", 0)]
    public async Task MissingCollectorSuppressesBackfillUntilAValidUnfilteredRun(
        string mode, int expectedUnits)
    {
        ImpactGranularity? granularity = mode switch
        {
            "test" => ImpactGranularity.Test,
            "class" => ImpactGranularity.Class,
            "off" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        _orchestrator.BackfillGranularity = granularity;
        _runner.ProduceCoverageReports = false;
        await StartAsync();
        await WaitForAsync(() => _state.ProjectCoverage.ContainsKey(Project));
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        var unavailable = _state.ProjectCoverage[Project];
        Assert.Equal(CoverageCollectionStatus.Unavailable, unavailable.Status);
        Assert.Equal("Collector missing.", unavailable.Message);
        Assert.Equal(_state.LastRun!.RunId, unavailable.RunId);
        Assert.Equal(TestRunStatus.Completed, _state.LastRun.Status);
        Assert.Equal(3, _state.LastRun.Tests.Count());
        Assert.All(_state.LastRun.Tests, test => Assert.Equal(TestStatus.Passed, test.Status));
        Assert.Empty(_processor.Recorded);
        Assert.All(_runner.Filters, filter => Assert.Null(filter));

        _runner.ProduceCoverageReports = true;
        var recovered = await _orchestrator.RequestRunAsync().WaitAsync(TimeSpan.FromSeconds(10), _ct);
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.Equal(TestRunStatus.Completed, recovered!.Status);
        Assert.Equal(CoverageCollectionStatus.Measured, _state.ProjectCoverage[Project].Status);
        Assert.Equal(recovered.RunId, _state.ProjectCoverage[Project].RunId);
        Assert.Equal(expectedUnits, _processor.Recorded.Count);
        if (granularity is not null)
        {
            Assert.All(_processor.Recorded, record => Assert.Equal(granularity, record.Granularity));
        }

        var filters = _runner.Filters.OfType<string>().Order(StringComparer.Ordinal);
        Assert.Equal(granularity switch
        {
            ImpactGranularity.Test =>
            [
                "FullyQualifiedName=A.Tests.Bar.One",
                "FullyQualifiedName=A.Tests.Foo.One",
                "FullyQualifiedName=A.Tests.Foo.Two",
            ],
            ImpactGranularity.Class =>
            [
                "FullyQualifiedName~A.Tests.Bar.",
                "FullyQualifiedName~A.Tests.Foo.",
            ],
            _ => Array.Empty<string>(),
        }, filters);
    }

    [Fact]
    public async Task InvalidReportFailsMeasurementWithoutFailingTestsAndRecoversOnNextFullRun()
    {
        _processor.ReportError = "Invalid coverage XML.";
        await StartAsync();
        await WaitForAsync(() => _state.ProjectCoverage.ContainsKey(Project));
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.Equal(TestRunStatus.Completed, _state.LastRun!.Status);
        Assert.Equal(3, _state.LastRun.Tests.Count());
        Assert.All(_state.LastRun.Tests, test => Assert.Equal(TestStatus.Passed, test.Status));
        Assert.Equal(CoverageCollectionStatus.Failed, _state.ProjectCoverage[Project].Status);
        Assert.Equal("Invalid coverage XML.", _state.ProjectCoverage[Project].Message);
        Assert.Empty(_processor.Recorded);

        _processor.ReportError = null;
        var recovered = await _orchestrator.RequestRunAsync().WaitAsync(TimeSpan.FromSeconds(10), _ct);
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.Equal(CoverageCollectionStatus.Measured, _state.ProjectCoverage[Project].Status);
        Assert.Equal(recovered!.RunId, _state.ProjectCoverage[Project].RunId);
        Assert.Equal(2, _processor.Recorded.Count);
    }

    [Fact]
    public async Task PartialMultiTargetReportsSuppressBackfillAndCannotReplaceFullEvidence()
    {
        const string issue = "The test run produced 1 coverage report(s) for 2 target frameworks.";
        _runner.ReportIssue = issue;
        await StartAsync();
        await WaitForAsync(() => _state.ProjectCoverage.ContainsKey(Project));
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.Equal(TestRunStatus.Completed, _state.LastRun!.Status);
        Assert.Equal(CoverageCollectionStatus.Partial, _state.ProjectCoverage[Project].Status);
        Assert.Equal(issue, _state.ProjectCoverage[Project].Message);
        Assert.False(Assert.Single(_processor.Processed).IsFull);
        Assert.Empty(_processor.Recorded);
        Assert.All(_runner.Filters, filter => Assert.Null(filter));

        _runner.ReportIssue = null;
        var recovered = await _orchestrator.RequestRunAsync().WaitAsync(TimeSpan.FromSeconds(10), _ct);
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.Equal(TestRunStatus.Completed, recovered!.Status);
        Assert.Equal(CoverageCollectionStatus.Measured, _state.ProjectCoverage[Project].Status);
        Assert.True(_processor.Processed.Last().IsFull);
        Assert.Equal(2, _processor.Recorded.Count);
    }

    [Theory]
    [InlineData(2, "The test run produced 1 coverage report(s) for 2 target frameworks.")]
    [InlineData(0, "The number of target frameworks could not be verified.")]
    public async Task MissingFrameworkResultsCannotMakeOneReportACompleteMeasurement(
        int frameworkCount, string expectedIssue)
    {
        _analyzer.TargetFrameworkCount = frameworkCount;
        _runner.TargetFramework = "net8.0";
        await StartAsync();
        await WaitForAsync(() => _state.ProjectCoverage.ContainsKey(Project));
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.Equal(TestRunStatus.Completed, _state.LastRun!.Status);
        Assert.All(_state.LastRun.Tests, test =>
        {
            Assert.Equal(TestStatus.Passed, test.Status);
            Assert.Equal("net8.0", test.TargetFramework);
        });
        Assert.Equal(3, _state.LastRun.Tests.Count());
        Assert.Equal(CoverageCollectionStatus.Partial, _state.ProjectCoverage[Project].Status);
        Assert.Equal(expectedIssue, _state.ProjectCoverage[Project].Message);
        Assert.False(Assert.Single(_processor.Processed).IsFull);
        Assert.True(Assert.Single(_processor.Processed[0].Impact!.Units).Merge);
        Assert.Empty(_processor.Recorded);
        Assert.All(_runner.Filters, filter => Assert.Null(filter));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackfillRejectsReportedPartialCoverageAndFrameworkReportShortfalls(bool shortfall)
    {
        _analyzer.TargetFrameworkCount = shortfall ? 2 : 1;
        _runner.MultiTargetForegroundReports = shortfall;
        _runner.FilteredReportIssue = shortfall ? null : "Only part of the coverage was collected.";
        await StartAsync();
        await WaitForAsync(() => _state.ProjectCoverage.ContainsKey(Project));
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.Equal(CoverageCollectionStatus.Measured, _state.ProjectCoverage[Project].Status);
        Assert.True(Assert.Single(_processor.Processed).IsFull);
        Assert.Equal(2, _runner.Filters.Count(filter => filter is not null));
        Assert.Empty(_processor.Recorded);
        Assert.Empty(_store.GetUnits(Project, ImpactGranularity.Class));
    }

    [Fact]
    public async Task SkippedTestsRemainInRunCoverageButAreNotIndividuallyMeasuredByBackfill()
    {
        _orchestrator.BackfillGranularity = ImpactGranularity.Test;
        _runner.SkippedTest = "A.Tests.Foo.Two";
        _runner.FailedTest = "A.Tests.Bar.One";
        await StartAsync();
        await WaitForAsync(() => _state.ProjectCoverage.ContainsKey(Project));
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        var statuses = _state.LastRun!.Tests.ToDictionary(test => test.FullyQualifiedName, test => test.Status);
        Assert.Equal(TestStatus.Skipped, statuses["A.Tests.Foo.Two"]);
        Assert.Equal(TestStatus.Failed, statuses["A.Tests.Bar.One"]);
        Assert.Equal(TestStatus.Passed, statuses["A.Tests.Foo.One"]);
        var processed = Assert.Single(_processor.Processed);
        Assert.Equal(["A.Tests.Bar.One", "A.Tests.Foo.One", "A.Tests.Foo.Two"],
            processed.Tests.Order(StringComparer.Ordinal));
        Assert.Equal(["A.Tests.Bar.One", "A.Tests.Foo.One", "A.Tests.Foo.Two"],
            Assert.Single(processed.Impact!.Units).TestFqns.Order(StringComparer.Ordinal));
        Assert.All(_processor.Recorded, record => Assert.Equal(ImpactGranularity.Test, record.Granularity));
        Assert.Equal(["A.Tests.Bar.One", "A.Tests.Foo.One"],
            _processor.Recorded.SelectMany(record => record.Units).Select(unit => unit.UnitKey).Order(StringComparer.Ordinal));
        Assert.Contains("FullyQualifiedName=A.Tests.Foo.Two", _runner.Filters);
        Assert.DoesNotContain(_store.GetUnits(Project, ImpactGranularity.Test),
            unit => unit.UnitKey == "A.Tests.Foo.Two");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BackfillDoesNotCreditATestWhenOneTargetFrameworkSkippedOrOmittedIt(bool skipped)
    {
        _orchestrator.BackfillGranularity = ImpactGranularity.Test;
        _analyzer.TargetFrameworkCount = 2;
        _runner.MultiTargetTests = true;
        _runner.MultiTargetForegroundReports = true;
        _runner.MultiTargetFilteredReports = true;
        _runner.FilteredSkippedFramework = skipped ? "net9.0" : null;
        _runner.FilteredMissingFramework = skipped ? null : "net9.0";

        await StartAsync();
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.Equal(CoverageCollectionStatus.Measured, _state.ProjectCoverage[Project].Status);
        Assert.Equal(6, _state.LastRun!.Tests.Count());
        Assert.All(_state.LastRun.Tests, test => Assert.Equal(TestStatus.Passed, test.Status));
        Assert.Equal(3, _runner.Filters.Count(filter => filter is not null));
        Assert.Empty(_processor.Recorded);
        Assert.Empty(_store.GetUnits(Project, ImpactGranularity.Test));
    }

    [Fact]
    public async Task BackfillPublishesImpactUpdatesAsResultsUpdates()
    {
        var publishing = new List<bool>();
        _processor.OnRecord = () => publishing.Add(_state.IsPublishingResults);

        await StartAsync();
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.Equal([true, true], publishing);
        Assert.False(_state.IsPublishingResults);
    }

    [Fact]
    public async Task CoverageStorageFailureRemainsVisibleUntilEngineRestart()
    {
        _processor.ProcessingFailure = "database locked";
        await StartAsync();
        await WaitForAsync(() => _state.ProjectCoverage.ContainsKey(Project));
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.Equal(TestRunStatus.Completed, _state.LastRun!.Status);
        Assert.Equal(3, _state.LastRun.Tests.Count());
        Assert.All(_state.LastRun.Tests, test => Assert.Equal(TestStatus.Passed, test.Status));
        Assert.Contains("database locked", _state.CoverageError, StringComparison.Ordinal);
        Assert.Equal(CoverageCollectionStatus.Failed, _state.ProjectCoverage[Project].Status);
        Assert.Empty(_processor.Recorded);
        Assert.Contains("database locked",
            Assert.Throws<InvalidOperationException>(() => _orchestrator.GetCoverageReport()).Message,
            StringComparison.Ordinal);

        _orchestrator.ClearResults(_root);
        Assert.Contains("database locked", _state.CoverageError, StringComparison.Ordinal);
        Assert.Empty(_state.ProjectCoverage);

        _processor.ProcessingFailure = null;
        var next = await _orchestrator.RequestRunAsync().WaitAsync(TimeSpan.FromSeconds(10), _ct);
        await _orchestrator.BackfillTask.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.Equal(TestRunStatus.Completed, next!.Status);
        Assert.Contains("database locked", _state.CoverageError, StringComparison.Ordinal);
        Assert.Equal(CoverageCollectionStatus.Failed, _state.ProjectCoverage[Project].Status);
        Assert.Equal(next.RunId, _state.ProjectCoverage[Project].RunId);
        _ = Assert.Single(_processor.Processed);
        Assert.Empty(_processor.Recorded);
    }

    [Fact]
    public async Task FilteredRunCannotEstablishCompleteCoverageUntilAnUnfilteredRun()
    {
        _orchestrator.BackfillGranularity = null;
        _state.TestFilter = "FullyQualifiedName~A.Tests.Foo.";
        await StartAsync();
        await WaitForAsync(() => _state.ProjectCoverage.ContainsKey(Project));

        Assert.Equal(CoverageCollectionStatus.Partial, _state.ProjectCoverage[Project].Status);
        Assert.Contains("unfiltered", _state.ProjectCoverage[Project].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["A.Tests.Foo.One", "A.Tests.Foo.Two"],
            _state.LastRun!.Tests.Select(test => test.FullyQualifiedName));

        _state.TestFilter = null;
        var full = await _orchestrator.RequestRunAsync().WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.Equal(CoverageCollectionStatus.Measured, _state.ProjectCoverage[Project].Status);
        Assert.Equal(full!.RunId, _state.ProjectCoverage[Project].RunId);
        _state.TestFilter = "FullyQualifiedName~A.Tests.Foo.";
        var filtered = await _orchestrator.RequestRunAsync().WaitAsync(TimeSpan.FromSeconds(10), _ct);
        Assert.Equal(CoverageCollectionStatus.Measured, _state.ProjectCoverage[Project].Status);
        Assert.Equal(filtered!.RunId, _state.ProjectCoverage[Project].RunId);
    }

    [Fact]
    public async Task SourceChangeDuringRunMakesCoveragePartialUntilFreshMeasurement()
    {
        _orchestrator.BackfillGranularity = null;
        await StartAsync();
        await WaitForAsync(() => _state.ProjectCoverage.ContainsKey(Project));
        Assert.Equal(CoverageCollectionStatus.Measured, _state.ProjectCoverage[Project].Status);
        _runner.OnForegroundRun = () => File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Code { int Value; }");

        var stale = await _orchestrator.RequestRunAsync().WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.Equal(ResultFreshness.Stale, stale!.Freshness);
        Assert.Equal(CoverageCollectionStatus.Partial, _state.ProjectCoverage[Project].Status);
        Assert.Contains("could not be verified", _state.ProjectCoverage[Project].Message, StringComparison.Ordinal);
        _runner.OnForegroundRun = null;
        var fresh = await _orchestrator.RequestRunAsync().WaitAsync(TimeSpan.FromSeconds(10), _ct);
        Assert.Equal(ResultFreshness.Current, fresh!.Freshness);
        Assert.Equal(CoverageCollectionStatus.Measured, _state.ProjectCoverage[Project].Status);
        Assert.Equal(fresh.RunId, _state.ProjectCoverage[Project].RunId);
    }

    [Fact]
    public async Task ClearResultsForgetsProjectMeasurementState()
    {
        _orchestrator.BackfillGranularity = null;
        await StartAsync();
        await WaitForAsync(() => _state.ProjectCoverage.ContainsKey(Project));
        Assert.Equal(CoverageCollectionStatus.Measured, _state.ProjectCoverage[Project].Status);

        _orchestrator.ClearResults(_root);

        Assert.Empty(_state.ProjectCoverage);
        Assert.False(_state.HasCoverageData);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResultsDirectoryIsRemovedAfterAttachmentsAreRetained(bool hasReport)
    {
        var results = Directory.CreateTempSubdirectory("piston-").FullName;
        try
        {
            var attachmentPath = Path.Combine(results, "attachment.txt");
            File.WriteAllText(attachmentPath, "retained test attachment");
            var reportPath = Path.Combine(results, "coverage.cobertura.xml");
            if (hasReport)
            {
                File.WriteAllText(reportPath, "fake report consumed by the fake processor");
            }

            var sawReportDuringProcessing = false;
            _orchestrator.BackfillGranularity = null;
            _orchestrator.ArtifactStore = new TestArtifactStore(_root);
            await StartAsync();
            await WaitForAsync(() => _state.ProjectCoverage.ContainsKey(Project));

            _runner.ProduceCoverageReports = hasReport;
            _runner.ReportPath = reportPath;
            _runner.TemporaryDirectory = results;
            _runner.AttachmentPath = attachmentPath;
            _processor.OnProcess = () => sawReportDuringProcessing = File.Exists(reportPath);
            var run = await _orchestrator.RequestRunAsync().WaitAsync(TimeSpan.FromSeconds(10), _ct);

            Assert.Equal(hasReport, sawReportDuringProcessing);
            Assert.False(Directory.Exists(results));
            var attachment = Assert.Single(run!.Tests.SelectMany(test => test.Attachments));
            Assert.True(attachment.Retained);
            Assert.Equal("retained test attachment",
                File.ReadAllText(_orchestrator.ArtifactStore.Resolve(attachment)!));
            Assert.Equal(hasReport ? CoverageCollectionStatus.Measured : CoverageCollectionStatus.Unavailable,
                _state.ProjectCoverage[Project].Status);
        }
        finally
        {
            if (Directory.Exists(results))
            {
                Directory.Delete(results, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ResultsDirectoryIsRemovedWhenRunnerThrowsAfterReportingAProject()
    {
        var results = Directory.CreateTempSubdirectory("piston-").FullName;
        try
        {
            var report = Path.Combine(results, "coverage.cobertura.xml");
            File.WriteAllText(report, "report");
            _orchestrator.BackfillGranularity = null;
            await StartAsync();
            await WaitForAsync(() => _state.ProjectCoverage.ContainsKey(Project));
            _runner.ReportPath = report;
            _runner.TemporaryDirectory = results;
            _runner.ThrowAfterProjectCompleted = true;

            var error = await Assert.ThrowsAsync<IOException>(() => _orchestrator.RequestRunAsync());

            Assert.Contains("runner failed", error.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(results));
            Assert.Equal(TestRunStatus.Faulted, _state.LastRun!.Status);
        }
        finally
        {
            if (Directory.Exists(results))
            {
                Directory.Delete(results, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ARunPublishesItsCoverageAndResultsAsOneResultsUpdate()
    {
        _orchestrator.BackfillGranularity = null;
        var observed = new List<(bool Publishing, int Suites, long Version)>();
        _processor.OnProcess = () => observed.Add((_state.IsPublishingResults, _state.TestSuites.Count, _state.ResultsVersion));

        await StartAsync();

        var duringProcessing = Assert.Single(observed);
        Assert.True(duringProcessing.Publishing);
        Assert.Equal(0, duringProcessing.Suites);
        Assert.False(_state.IsPublishingResults);
        Assert.Equal(duringProcessing.Version + 1, _state.ResultsVersion);
        Assert.NotEmpty(_state.TestSuites);
    }

    [Fact]
    public async Task GetCoveringTests_MarksEvidenceRecordedAgainstOtherContentAsNotCurrent()
    {
        await _store.InitializeAsync(_root);
        var code = Path.Combine(_root, "Code.cs");
        await _store.RecordImpactAsync(new ImpactRecording(
            Project, ImpactGranularity.Run, "run", _store.CreateRunId(), ["A.Tests.Bar.One", "A.Tests.Foo.One"],
            [new ImpactFile(code, [new LineSpan(1, 2)], "class Code\n{ int Value = 1; }")], Merge: false));
        await _store.RecordImpactAsync(new ImpactRecording(
            Project, ImpactGranularity.Class, "A.Tests.Foo", _store.CreateRunId(), ["A.Tests.Foo.One"],
            [new ImpactFile(code, [new LineSpan(1, 1)], "class Code { }")], Merge: false));

        var tests = _orchestrator.GetCoveringTests(code)!;

        Assert.Equal(["A.Tests.Bar.One", "A.Tests.Foo.One"], tests.Select(t => t.TestFqn));
        Assert.Equal(("run", false), (tests[0].Granularity, tests[0].IsCurrent));
        Assert.Equal(("class", true), (tests[1].Granularity, tests[1].IsCurrent));
        Assert.Equal([new CoveredLineRange(1, 1)], tests[1].ExecutedLines);
        Assert.Empty(_orchestrator.GetCoveringTests(Path.Combine(_root, "Other.cs"))!);
    }

    private sealed class ClassRunner : ITestRunnerService
    {
        private static readonly string[] Tests = ["A.Tests.Foo.One", "A.Tests.Foo.Two", "A.Tests.Bar.One"];

        public List<string?> Filters { get; } = [];
        public bool BlockFiltered { get; set; }
        public bool ReportResourceWaits { get; set; }
        public bool ProduceCoverageReports { get; set; } = true;
        public string? ReportPath { get; set; }
        public string? ReportIssue { get; set; }
        public string? FilteredReportIssue { get; set; }
        public bool MultiTargetForegroundReports { get; set; }
        public bool MultiTargetFilteredReports { get; set; }
        public bool MultiTargetTests { get; set; }
        public string? FilteredSkippedFramework { get; set; }
        public string? FilteredMissingFramework { get; set; }
        public bool ThrowAfterProjectCompleted { get; set; }
        public string? SkippedTest { get; set; }
        public string? FailedTest { get; set; }
        public string? TargetFramework { get; set; }
        public string? TemporaryDirectory { get; set; }
        public string? AttachmentPath { get; set; }
        public Action? OnForegroundRun { get; set; }
        public TaskCompletionSource FilteredStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FilteredCancelled { get; private set; }

        public Task<TestRunResult> RunTestsAsync(string solutionPath, string? filter, Action<IReadOnlyList<TestSuite>>? onProgress, CancellationToken ct) =>
            RunTestsAsync(solutionPath, null, filter, false, onProgress, null, ct);

        public Task<TestRunResult> RunTestsAsync(
            string solutionPath, IReadOnlyList<string>? testProjectPaths, string? filter, bool collectCoverage,
            Action<IReadOnlyList<TestSuite>>? onProgress, CancellationToken ct) =>
            RunTestsAsync(solutionPath, testProjectPaths, filter, collectCoverage, onProgress, null, ct);

        public Task<TestRunResult> RunTestsAsync(
            string solutionPath, IReadOnlyList<string>? testProjectPaths, string? filter, bool collectCoverage,
            Action<IReadOnlyList<TestSuite>>? onProgress, Action<ProjectTestResult>? onProjectCompleted, CancellationToken ct) =>
            RunTestsAsync(solutionPath, testProjectPaths, filter, collectCoverage,
                new TestRunCallbacks { OnProjectCompleted = onProjectCompleted }, ct);

        public async Task<TestRunResult> RunTestsAsync(
            string solutionPath, IReadOnlyList<string>? testProjectPaths, string? filter, bool collectCoverage,
            TestRunCallbacks callbacks, CancellationToken ct)
        {
            lock (Filters)
            {
                Filters.Add(filter);
            }

            if (filter is not null && ReportResourceWaits)
            {
                callbacks.OnProjectWaiting?.Invoke(Project, new TestResourceWait(
                    TestResourceWaitState.Waiting, ["port:5432"], false,
                    [new TestResourceBlocker("port:5432", TestResourceBlockerKind.Held, 42, "/other", "/other/A.Tests.csproj", DateTimeOffset.UtcNow)],
                    0, 0, null, DateTimeOffset.UtcNow, null));
            }

            if (filter is not null && BlockFiltered)
            {
                _ = FilteredStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    FilteredCancelled = true;
                    throw;
                }
            }

            callbacks.OnProjectResourcesAcquired?.Invoke(Project);
            callbacks.OnProjectStarted?.Invoke(Project);
            if (filter is null)
            {
                OnForegroundRun?.Invoke();
            }

            var exact = filter?.StartsWith("FullyQualifiedName=", StringComparison.Ordinal) == true;
            var prefix = filter?.Replace("FullyQualifiedName~", "", StringComparison.Ordinal)
                .Replace("FullyQualifiedName=", "", StringComparison.Ordinal);
            string?[] frameworks = MultiTargetTests ? ["net8.0", "net9.0"] : [TargetFramework];
            var suite = new TestSuite("A.Tests",
                [.. Tests.Where(t => prefix is null || (exact
                    ? string.Equals(t, prefix, StringComparison.Ordinal)
                    : t.StartsWith(prefix, StringComparison.Ordinal)))
                    .SelectMany(t => frameworks
                        .Where(framework => filter is null || FilteredMissingFramework is null || framework != FilteredMissingFramework)
                        .Select(framework => new TestResult(t, t,
                            t == SkippedTest || (filter is not null && framework is not null && framework == FilteredSkippedFramework)
                                ? TestStatus.Skipped : t == FailedTest ? TestStatus.Failed : TestStatus.Passed,
                            TimeSpan.Zero, null, null, null, null)
                    {
                        TargetFramework = framework,
                        Attachments = t == "A.Tests.Foo.One" && AttachmentPath is not null
                            ? [new TestAttachment(AttachmentPath)] : [],
                    }))],
                DateTimeOffset.UtcNow, TimeSpan.Zero);
            string[] reports = collectCoverage && ProduceCoverageReports
                ? [ReportPath ?? $"/nowhere/{Guid.NewGuid():N}.cobertura.xml"] : [];
            if (reports.Length > 0 && (filter is null ? MultiTargetForegroundReports : MultiTargetFilteredReports))
            {
                reports = [reports[0], $"/nowhere/{Guid.NewGuid():N}.cobertura.xml"];
            }
            var project = new ProjectTestResult(Project, [suite], null, reports, Crashed: false)
            {
                CoverageIssue = (filter is null ? ReportIssue : FilteredReportIssue ?? ReportIssue)
                    ?? (collectCoverage && !ProduceCoverageReports ? "Collector missing." : null),
                TemporaryDirectories = TemporaryDirectory is null ? [] : [TemporaryDirectory],
            };
            callbacks.OnProjectCompleted?.Invoke(project);
            if (ThrowAfterProjectCompleted)
            {
                throw new IOException("runner failed after reporting a project");
            }

            return new TestRunResult([suite], null, reports) { Projects = [project] };
        }

        public Task DiscoverTestsAsync(
            string solutionPath, IReadOnlyList<string>? testProjectPaths, string? filter,
            Action<string, IReadOnlyList<TestSuite>> onDiscovered, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingProcessor : ICoverageProcessor
    {
        public List<CoverageImpactOptions> Recorded { get; } = [];
        public List<(IReadOnlyList<string> Tests, CoverageImpactOptions? Impact, bool IsFull)> Processed { get; } = [];

        public Action? OnProcess { get; set; }
        public Action? OnRecord { get; set; }
        public string? ReportError { get; set; }
        public string? ProcessingFailure { get; set; }

        public Task<CoverageProcessingResult> ProcessCoverageAsync(
            long runId, IReadOnlyList<string> coberturaXmlPaths, IReadOnlyList<string> testFqns, ICoverageStore store,
            bool isFullRun, string? sourceRoot, CoverageImpactOptions? impact)
        {
            OnProcess?.Invoke();
            Processed.Add((testFqns, impact, isFullRun));
            if (ProcessingFailure is { } failure)
            {
                throw new IOException(failure);
            }

            return Task.FromResult(Reports(coberturaXmlPaths));
        }

        public async Task<CoverageProcessingResult> RecordImpactAsync(long runId, CoverageImpactOptions impact, IImpactMapStore store, string? sourceRoot)
        {
            OnRecord?.Invoke();
            foreach (var unit in impact.Units)
            {
                await store.RecordImpactAsync(new ImpactRecording(
                    unit.ProjectPath, impact.Granularity, unit.UnitKey, runId, unit.TestFqns, [], unit.Merge)
                {
                    TestResultKeys = unit.TestResultKeys,
                });
            }

            lock (Recorded)
            {
                Recorded.Add(impact);
            }

            return Reports(impact.Units.SelectMany(unit => unit.ReportPaths));
        }

        private CoverageProcessingResult Reports(IEnumerable<string> paths) =>
            new(paths.Distinct(StringComparer.Ordinal)
                .ToDictionary(path => path, _ => ReportError is null
                    ? new CoverageReportResult(1, null)
                    : new CoverageReportResult(0, ReportError), StringComparer.Ordinal));
    }
}
