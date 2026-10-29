// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Piston.Integration;

internal enum Harness
{
    Copilot,
    Claude,
}

internal enum InitScope
{
    User,
    Repo,
}

internal sealed record InitOptions(Harness Harness, InitScope Scope, bool Write, bool Force, bool Dashboard);

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

/// <summary>
/// <c>piston init</c>: reports, and only with <see cref="InitOptions.Write"/> writes, a harness's Piston MCP
/// entry, skill and (for Copilot) dashboard canvas. Differing content is never overwritten without
/// <see cref="InitOptions.Force"/>, and only Piston's own MCP entry is touched in a shared config file.
/// </summary>
internal static class HarnessInit
{
    public const string ServerName = "piston";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Run(InitOptions options, InitLocations locations, TextWriter output)
    {
        var targets = Targets(options, locations);
        var statuses = targets.Select(t => (Target: t, Status: t.Inspect())).ToList();

        output.WriteLine($"Piston setup for {DisplayName(options.Harness)} ({options.Scope.ToString().ToLowerInvariant()} scope)");
        output.WriteLine();
        var entry = McpEntry(options);
        if (targets.OfType<McpEntryTarget>().FirstOrDefault() is { } mcp)
        {
            output.WriteLine($"MCP server entry, merged into \"mcpServers\" of {mcp.Path} (other servers are kept):");
        }
        else
        {
            output.WriteLine("MCP server entry:");
        }

        output.WriteLine(new JsonObject { [ServerName] = entry }.ToJsonString(JsonOptions));
        if (options is { Harness: Harness.Claude, Scope: InitScope.User })
        {
            output.WriteLine();
            output.WriteLine("Piston does not edit ~/.claude.json. Register the server with Claude Code yourself:");
            output.WriteLine($"  claude mcp add --scope user --transport stdio {ServerName} -- piston {string.Join(' ', McpArgs(options))}");
        }

        output.WriteLine();
        output.WriteLine("Files:");
        var blocked = false;
        foreach (var item in statuses)
        {
            var (target, status) = item;
            string verb;
            switch (status.Kind)
            {
                case TargetStatusKind.Create:
                    verb = options.Write ? "created" : "create";
                    if (options.Write)
                    {
                        target.Apply();
                    }

                    break;
                case TargetStatusKind.Unchanged:
                    verb = "unchanged";
                    break;
                case TargetStatusKind.Differs when options.Write && options.Force:
                    target.Apply();
                    verb = "overwritten";
                    break;
                case TargetStatusKind.Differs:
                    verb = options.Write ? "skipped" : "differs";
                    status = status with { Detail = options.Write ? "content differs (use --force)" : null };
                    blocked |= options.Write;
                    break;
                default:
                    verb = "invalid";
                    blocked = true;
                    break;
            }

            output.WriteLine($"  {verb,-11} {target.Description}{(status.Detail is { } detail ? $": {detail}" : "")}");
        }

        output.WriteLine();
        var differs = statuses.Any(s => s.Status.Kind == TargetStatusKind.Differs);
        if (!options.Write)
        {
            output.WriteLine("Dry run: nothing was written. Rerun with --write to apply" +
                (differs ? "; add --force to overwrite content that differs." : "."));
        }
        else if (blocked)
        {
            output.WriteLine("Some items were not written. Content that differs is kept unless you rerun with --force; " +
                "fix invalid files by hand.");
        }
        else
        {
            WriteNextSteps(options, output);
        }

        return blocked ? 1 : 0;
    }

    internal static JsonObject McpEntry(InitOptions options)
    {
        var args = new JsonArray([.. McpArgs(options).Select(a => (JsonNode)JsonValue.Create(a))]);
        // ~/.copilot/mcp-config.json uses Copilot's documented shape; a repository's .mcp.json is shared by Copilot
        // and Claude Code, which both accept the standard "stdio" type.
        return options is { Harness: Harness.Copilot, Scope: InitScope.User }
            ? new JsonObject { ["type"] = "local", ["command"] = "piston", ["args"] = args, ["tools"] = new JsonArray("*") }
            : new JsonObject { ["type"] = "stdio", ["command"] = "piston", ["args"] = args };
    }

    private static string[] McpArgs(InitOptions options) => options.Dashboard ? ["mcp", "--dashboard"] : ["mcp"];

    private static string DisplayName(Harness harness) => harness == Harness.Copilot ? "GitHub Copilot" : "Claude Code";

    private static List<InitTarget> Targets(InitOptions options, InitLocations locations)
    {
        var repo = locations.RepoDirectory;
        var targets = new List<InitTarget>();
        switch (options)
        {
            case { Harness: Harness.Copilot, Scope: InitScope.User }:
                targets.Add(new McpEntryTarget(Path.Combine(locations.CopilotHome, "mcp-config.json"), McpEntry(options)));
                targets.Add(SkillTarget(Path.Combine(locations.CopilotHome, "skills")));
                targets.AddRange(CanvasTargets(Path.Combine(locations.CopilotHome, "extensions")));
                break;
            case { Harness: Harness.Copilot, Scope: InitScope.Repo }:
                targets.Add(new McpEntryTarget(Path.Combine(repo, ".mcp.json"), McpEntry(options)));
                targets.Add(SkillTarget(Path.Combine(repo, ".github", "skills")));
                targets.AddRange(CanvasTargets(Path.Combine(repo, ".github", "extensions")));
                break;
            case { Harness: Harness.Claude, Scope: InitScope.User }:
                targets.Add(SkillTarget(Path.Combine(locations.ClaudeHome, "skills")));
                break;
            default:
                targets.Add(new McpEntryTarget(Path.Combine(repo, ".mcp.json"), McpEntry(options)));
                targets.Add(SkillTarget(Path.Combine(repo, ".claude", "skills")));
                break;
        }

        return targets;
    }

    private static FileTarget SkillTarget(string skillsDirectory) =>
        new(Path.Combine(skillsDirectory, HarnessAssets.SkillName, "SKILL.md"), HarnessAssets.Skill);

    private static IEnumerable<InitTarget> CanvasTargets(string extensionsDirectory) =>
        HarnessAssets.CanvasFiles.Select(file =>
            new FileTarget(Path.Combine(extensionsDirectory, HarnessAssets.CanvasName, file.Name), file.Content));

    private static void WriteNextSteps(InitOptions options, TextWriter output)
    {
        output.WriteLine("Done.");
        if (options.Harness == Harness.Copilot)
        {
            output.WriteLine("Start a new Copilot session (or reload extensions) to load the skill and the Piston canvas.");
            if (!options.Dashboard)
            {
                output.WriteLine("The canvas needs a dashboard: rerun with --dashboard, or set \"dashboard\": true in .piston.json.");
            }
        }
        else
        {
            output.WriteLine("Start a new Claude Code session to load the skill" +
                (options.Scope == InitScope.User ? " after registering the MCP server." : " and the MCP server."));
        }
    }

    private static void WriteAtomically(string path, byte[] content)
    {
        // Write through a symbolic link (for example a dotfiles-managed config) rather than replacing it.
        if (File.Exists(path) && new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true) is { } target)
        {
            path = target.FullName;
        }

        var directory = Path.GetDirectoryName(path)!;
        _ = Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        UnixFileMode? mode = null;
        if (!OperatingSystem.IsWindows() && File.Exists(path))
        {
            // The existing file may hold other servers' credentials: keep the temporary copy private until it
            // carries the destination's mode, rather than creating it with the umask's default.
            mode = File.GetUnixFileMode(path);
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(content);
            }

            if (mode is { } existing && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, existing);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private enum TargetStatusKind
    {
        Create,
        Unchanged,
        Differs,
        Invalid,
    }

    private sealed record TargetStatus(TargetStatusKind Kind, string? Detail = null);

    private abstract class InitTarget(string path)
    {
        public string Path { get; } = path;

        public virtual string Description => Path;

        public abstract TargetStatus Inspect();

        public abstract void Apply();
    }

    private sealed class FileTarget(string path, byte[] content) : InitTarget(path)
    {
        public override TargetStatus Inspect() =>
            !File.Exists(Path) ? new(TargetStatusKind.Create)
            : File.ReadAllBytes(Path).AsSpan().SequenceEqual(content) ? new(TargetStatusKind.Unchanged)
            : new(TargetStatusKind.Differs);

        public override void Apply() => WriteAtomically(Path, content);
    }

    private sealed class McpEntryTarget(string path, JsonObject entry) : InitTarget(path)
    {
        public override string Description => $"{Path} (mcpServers.{ServerName})";

        public override TargetStatus Inspect()
        {
            var (root, error) = Load();
            if (error is not null)
            {
                return new(TargetStatusKind.Invalid, error);
            }

            return root?["mcpServers"]?[ServerName] switch
            {
                null => new(TargetStatusKind.Create),
                var existing when JsonNode.DeepEquals(existing, entry) => new(TargetStatusKind.Unchanged),
                _ => new(TargetStatusKind.Differs),
            };
        }

        public override void Apply()
        {
            var (root, error) = Load();
            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }

            root ??= [];
            if (root["mcpServers"] is not JsonObject servers)
            {
                servers = [];
                root["mcpServers"] = servers;
            }

            servers[ServerName] = entry.DeepClone();
            WriteAtomically(Path, System.Text.Encoding.UTF8.GetBytes(root.ToJsonString(JsonOptions) + "\n"));
        }

        private (JsonObject? Root, string? Error) Load()
        {
            if (!File.Exists(Path))
            {
                return (null, null);
            }

            var text = File.ReadAllText(Path);
            if (string.IsNullOrWhiteSpace(text))
            {
                return (null, null);
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(text);
            }
            catch (JsonException ex)
            {
                return (null, $"not valid JSON ({ex.Message}); add the entry by hand");
            }

            if (node is not JsonObject root)
            {
                return (null, "not a JSON object; add the entry by hand");
            }

            return root["mcpServers"] switch
            {
                JsonObject => (root, null),
                null when root.Count == 0 => (root, null),
                null => (null, "has no \"mcpServers\" object; add the entry by hand"),
                _ => (null, "\"mcpServers\" is not an object; add the entry by hand"),
            };
        }
    }
}
