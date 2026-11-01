// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Hosting.Configuration;

/// <summary>A <c>testResources</c> entry of <c>.piston.json</c>.</summary>
public sealed class PistonTestResourceConfig
{
    /// <summary>Property names an entry may use; anything else is rejected.</summary>
    public static readonly IReadOnlyList<string> PropertyNames = ["projects", "exclusive", "containers", "isolation"];

    /// <summary>Glob over project paths relative to the solution directory, e.g. <c>tests/**/*.IntegrationTests.csproj</c>.</summary>
    public string? Projects { get; set; }

    /// <summary>Exclusive resources: <c>port:5432</c>, <c>container:orders-db</c>, <c>resource:shared-queue</c>.</summary>
    public List<string>? Exclusive { get; set; }

    /// <summary>Overrides whether the projects use containers (counted against the machine's <c>containerSlots</c>).</summary>
    public bool? Containers { get; set; }

    /// <summary>"repository" (one run per repository at a time) or "none".</summary>
    public string? Isolation { get; set; }
}
