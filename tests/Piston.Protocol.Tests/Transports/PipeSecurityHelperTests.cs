using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Piston.Protocol.Transports;
using Xunit;

namespace Piston.Protocol.Tests.Transports;

public sealed class PipeSecurityHelperTests
{
    [Fact]
    public void CreateCurrentUserOnly_OnNonWindows_ReturnsNull()
    {
        if (OperatingSystem.IsWindows())
            return; // Skip on Windows — tested by Windows-specific tests below

        var result = PipeSecurityHelper.CreateCurrentUserOnly();

        Assert.Null(result);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void CreateCurrentUserOnly_OnWindows_ReturnsNonNull()
    {
        if (!OperatingSystem.IsWindows())
            return; // Skip on non-Windows

        var result = PipeSecurityHelper.CreateCurrentUserOnly();

        Assert.NotNull(result);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void CreateCurrentUserOnly_OnWindows_ContainsCurrentUserAllowRule()
    {
        if (!OperatingSystem.IsWindows())
            return; // Skip on non-Windows

        var currentUser = WindowsIdentity.GetCurrent().User!;
        var security    = PipeSecurityHelper.CreateCurrentUserOnly();

        Assert.NotNull(security);

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, targetType: typeof(SecurityIdentifier));
        var userAllowRule = rules
            .Cast<PipeAccessRule>()
            .FirstOrDefault(r =>
                r.IdentityReference.Equals(currentUser) &&
                r.AccessControlType == AccessControlType.Allow &&
                r.PipeAccessRights.HasFlag(PipeAccessRights.FullControl));

        Assert.NotNull(userAllowRule);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void CreateCurrentUserOnly_OnWindows_OnlyCurrentUserHasAccess()
    {
        if (!OperatingSystem.IsWindows())
            return; // Skip on non-Windows

        var currentUser = WindowsIdentity.GetCurrent().User!;
        var security    = PipeSecurityHelper.CreateCurrentUserOnly();

        Assert.NotNull(security);

        // Only the current user rule should be present — no other allow rules
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, targetType: typeof(SecurityIdentifier));
        var allowRules = rules.Cast<PipeAccessRule>().Where(r => r.AccessControlType == AccessControlType.Allow).ToList();

        Assert.Single(allowRules);
        Assert.Equal(currentUser, allowRules[0].IdentityReference);
    }
}

