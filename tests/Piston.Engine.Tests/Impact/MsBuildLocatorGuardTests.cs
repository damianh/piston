// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using Piston.Engine.Impact;
using Xunit;

namespace Piston.Engine.Tests.Impact;

public sealed class MsBuildLocatorGuardTests
{
    [Fact]
    public void RestoreOriginalEnvironment_RemovesLocatorExportsFromChildDotnet()
    {
        MsBuildLocatorGuard.EnsureRegistered();
        var original = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(MsBuildLocatorGuard.OriginalEnvironment);
        // Registration exports the location of the SDK this process loaded.
        Assert.NotNull(Environment.GetEnvironmentVariable("MSBuildSDKsPath"));

        var psi = new ProcessStartInfo("dotnet");
        MsBuildLocatorGuard.RestoreOriginalEnvironment(psi);

        foreach (var name in MsBuildLocatorGuard.LocatorEnvironmentVariables)
        {
            _ = psi.Environment.TryGetValue(name, out var value);
            Assert.Equal(original[name], value);
        }
    }
}
