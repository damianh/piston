// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;
using Piston.Web.Models;
using Xunit;

namespace Piston.Tests.Web;

public sealed class ResourceWaitTextTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static TestResourceWaitDto Wait(string state, params TestResourceBlockerDto[] blockers) =>
        new(state, ["port:5432"], true, blockers, 0, 2, 2, T0, null, null);

    [Fact]
    public void BySuiteName_KeepsOnlyWaitingProjects()
    {
        var waits = new Dictionary<string, TestResourceWaitDto>
        {
            ["/w/tests/Db.Tests.csproj"] = Wait("Waiting"),
            ["/w/tests/Api.Tests.csproj"] = Wait("Acquired"),
        };

        Assert.Equal(["Db.Tests"], ResourceWaitText.BySuiteName(waits).Keys);
        Assert.Empty(ResourceWaitText.BySuiteName(null));
    }

    [Fact]
    public void Describe_ListsNeedsSlotsAndBlockers()
    {
        var wait = Wait("Waiting",
            new("port:5432", "Held", 42, "/wt/other", "/wt/other/tests/Db.Tests.csproj", T0),
            new("containerSlots", "Queued", 7, null, null, T0));

        var lines = ResourceWaitText.Describe("/w/tests/Db.Tests.csproj", wait, T0.AddSeconds(5)).Split(Environment.NewLine);

        Assert.StartsWith("Db.Tests waiting ", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(" for exclusive test resources", lines[0], StringComparison.Ordinal);
        Assert.Equal(
            [
                "Needs: port:5432",
                "Container slots: 2/2 in use",
                "Blocked: port:5432 held by Db.Tests in /wt/other (pid 42)",
                "Blocked: container slots (2/2 in use) queued ahead for a test project (pid 7)",
            ],
            lines[1..]);
    }
}
