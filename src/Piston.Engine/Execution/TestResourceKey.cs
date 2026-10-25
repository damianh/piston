// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Piston.Engine.Execution;

/// <summary>
/// Names of exclusive test resources. A key is <c>kind:value</c> with a lower-case kind:
/// <c>port:&lt;1-65535&gt;</c>, <c>container:&lt;docker name&gt;</c>, <c>resource:&lt;name&gt;</c>, and the
/// inferred <c>project:&lt;repository&gt;/&lt;path&gt;</c> that isolates a project across worktrees.
/// </summary>
public static partial class TestResourceKey
{
    public const string PortPrefix = "port:";
    public const string ContainerPrefix = "container:";
    public const string ResourcePrefix = "resource:";
    public const string ProjectPrefix = "project:";

    private const int MaxValueLength = 200;

    /// <summary>
    /// Validates and normalizes a key declared in configuration (<c>port</c>, <c>container</c> or
    /// <c>resource</c>). Returns false with a reason when it is invalid.
    /// </summary>
    public static bool TryNormalize(string? raw, [NotNullWhen(true)] out string? key, [NotNullWhen(false)] out string? error)
    {
        key = null;
        var text = raw?.Trim() ?? "";
        var colon = text.IndexOf(':');
        if (colon <= 0)
        {
            error = $"'{raw}' must be 'port:<number>', 'container:<name>' or 'resource:<name>'.";
            return false;
        }

        var kind = text[..(colon + 1)].ToLowerInvariant();
        var value = text[(colon + 1)..].Trim();
        switch (kind)
        {
            case PortPrefix:
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
                {
                    error = $"'{raw}' must name a port between 1 and 65535.";
                    return false;
                }

                key = PortPrefix + port.ToString(CultureInfo.InvariantCulture);
                error = null;
                return true;

            case ContainerPrefix:
                if (value.Length > MaxValueLength || !ContainerName().IsMatch(value))
                {
                    error = $"'{raw}' must name a container: a letter or digit followed by letters, digits, '_', '.' or '-'.";
                    return false;
                }

                key = ContainerPrefix + value;
                error = null;
                return true;

            case ResourcePrefix:
                if (value.Length == 0 || value.Length > MaxValueLength || value.Any(char.IsControl))
                {
                    error = $"'{raw}' must name a resource of 1-{MaxValueLength} printable characters.";
                    return false;
                }

                key = ResourcePrefix + value;
                error = null;
                return true;

            default:
                error = $"'{raw}' has unknown kind '{text[..colon]}'; use 'port', 'container' or 'resource'.";
                return false;
        }
    }

    /// <summary>The port a <c>port:</c> key names, or null for other keys.</summary>
    public static int? TryGetPort(string key) =>
        key.StartsWith(PortPrefix, StringComparison.Ordinal)
        && int.TryParse(key.AsSpan(PortPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            ? port
            : null;

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex ContainerName();
}
