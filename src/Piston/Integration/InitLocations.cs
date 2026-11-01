// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Integration;

/// <summary>Where each scope's assets go: the repository directory and the harnesses' user directories.</summary>
internal sealed record InitLocations(string RepoDirectory, string CopilotHome, string ClaudeHome)
{
    public static InitLocations FromEnvironment(string repoDirectory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new(
            repoDirectory,
            NonEmpty(Environment.GetEnvironmentVariable("COPILOT_HOME")) ?? Path.Combine(home, ".copilot"),
            NonEmpty(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")) ?? Path.Combine(home, ".claude"));
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
