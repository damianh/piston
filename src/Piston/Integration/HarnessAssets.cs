// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Integration;

/// <summary>The harness integration assets embedded in the tool (see Piston.csproj).</summary>
internal static class HarnessAssets
{
    private const string SkillResource = "Piston.Integration/skill/SKILL.md";
    private const string CanvasPrefix = "Piston.Integration/canvas/";

    public const string SkillName = "piston";

    public const string CanvasName = "piston-dashboard";

    public static byte[] Skill => Read(SkillResource);

    public static IReadOnlyList<(string Name, byte[] Content)> CanvasFiles { get; } =
        [.. typeof(HarnessAssets).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(CanvasPrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name => (name[CanvasPrefix.Length..], Read(name)))];

    private static byte[] Read(string name)
    {
        using var stream = typeof(HarnessAssets).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded asset '{name}' is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
