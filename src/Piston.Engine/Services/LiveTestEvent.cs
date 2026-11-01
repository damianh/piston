// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// A live test event: <see cref="TestStatus.Running"/> on start, or the outcome on end. The test case's
/// <paramref name="FullyQualifiedName"/>, when known, qualifies the display name into its <see cref="Identity"/>.
/// </summary>
internal sealed record LiveTestEvent(string DisplayName, TestStatus Status, string? FullyQualifiedName = null)
{
    /// <summary>The test's identity, matching the one <see cref="TrxResultParser"/> gives its final result.</summary>
    public string Identity => FullyQualifiedName is null ? DisplayName : TestIdentity.Of(FullyQualifiedName, DisplayName);
}
