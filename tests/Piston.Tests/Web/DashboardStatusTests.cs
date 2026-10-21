// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;
using Piston.Protocol.Messages;
using Piston.Web.Models;
using Xunit;

namespace Piston.Tests.Web;

public class DashboardStatusTests
{
    private static readonly TestSuiteDto[] Suites =
    [
        Suite("Alpha.Tests", Test("A.One", TestStatusDto.Failed), Test("A.Two", TestStatusDto.Passed)),
        Suite("Beta.Tests",
            Test("B.One", TestStatusDto.Failed, "net8.0"),
            Test("B.One", TestStatusDto.Failed, "net10.0"),
            Test("B.Two", TestStatusDto.Skipped)),
        Suite("Gamma.Tests", Test("C.One", TestStatusDto.Passed)),
    ];

    [Fact]
    public void RunFailed_TargetsFailedTestsAndTheirProjects()
    {
        var command = DashboardStatus.RunFailed(Suites);

        Assert.NotNull(command);
        Assert.Equal(["Alpha.Tests", "Beta.Tests"], command.Projects);
        Assert.Equal(["A.One", "B.One"], command.Tests!);
        Assert.Equal(2, DashboardStatus.FailedCount(Suites));
    }

    [Fact]
    public void RunFailed_NoFailures_ReturnsNull()
    {
        TestSuiteDto[] suites = [Suite("Gamma.Tests", Test("C.One", TestStatusDto.Passed))];

        Assert.Null(DashboardStatus.RunFailed(suites));
        Assert.Equal(0, DashboardStatus.FailedCount(suites));
    }

    [Fact]
    public void Title_LeadsWithStateThenSolution()
    {
        Assert.Equal("✖ 3 failed · Piston · Piston", DashboardStatus.Title(Snapshot(PistonPhaseDto.Watching, Suites), true));
        Assert.Equal("⟳ Building · Piston · Piston", DashboardStatus.Title(Snapshot(PistonPhaseDto.Building, Suites), true));
        Assert.Equal("Disconnected · Piston · Piston", DashboardStatus.Title(Snapshot(PistonPhaseDto.Idle, Suites), false));
        Assert.Equal("Piston", DashboardStatus.Title(null, true));
    }

    [Theory]
    [InlineData(PistonPhaseDto.Watching, true, "fail")]
    [InlineData(PistonPhaseDto.Testing, true, "busy")]
    [InlineData(PistonPhaseDto.Error, true, "fail")]
    [InlineData(PistonPhaseDto.Watching, false, "idle")]
    public void Icon_ReflectsPhaseAndResults(PistonPhaseDto phase, bool connected, string expected) =>
        Assert.Equal(expected, DashboardStatus.Icon(Snapshot(phase, Suites), connected));

    [Fact]
    public void Icon_AllPassed_IsPass()
    {
        TestSuiteDto[] suites = [Suite("Gamma.Tests", Test("C.One", TestStatusDto.Passed))];

        Assert.Equal("pass", DashboardStatus.Icon(Snapshot(PistonPhaseDto.Watching, suites), true));
    }

    [Fact]
    public void Announcement_StartAndFinish()
    {
        var watching = Snapshot(PistonPhaseDto.Watching, Suites);
        var building = Snapshot(PistonPhaseDto.Building, Suites);
        var testing = Snapshot(PistonPhaseDto.Testing, Suites);

        Assert.Equal("Run started.", DashboardStatus.Announcement(watching, building));
        Assert.Null(DashboardStatus.Announcement(building, testing));
        Assert.Equal("Run finished: 3 failed, 2 passed, 1 skipped.", DashboardStatus.Announcement(testing, watching));
        Assert.Equal("Run finished: the build failed.",
            DashboardStatus.Announcement(building, Snapshot(PistonPhaseDto.Error, Suites)));
        Assert.Null(DashboardStatus.Announcement(watching, watching));
        Assert.Null(DashboardStatus.Announcement(null, watching));
    }

    private static TestResultDto Test(string fqn, TestStatusDto status, string? framework = null) =>
        new(fqn, fqn, status, 1, null, null, null, null, framework);

    private static TestSuiteDto Suite(string name, params TestResultDto[] tests) =>
        new(name, tests, DateTimeOffset.UnixEpoch, 0);

    private static StateSnapshotNotification Snapshot(PistonPhaseDto phase, IReadOnlyList<TestSuiteDto> suites) =>
        new(phase, suites, [], null, null, null, null, null, 0, 0, 0, null, "/repo/Piston.slnx",
            null, null, null, false, false, null, 0, 0, null);
}
