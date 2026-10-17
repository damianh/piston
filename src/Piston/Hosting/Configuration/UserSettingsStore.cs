// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;

namespace Piston.Hosting.Configuration;

/// <summary>
/// Reads and writes the machine-wide user settings file shared by every daemon of the user.
/// The file is re-read on every access so edits by hand or by another daemon are picked up.
/// A missing file, invalid JSON or invalid values yield defaults on read. Updates merge into the
/// existing JSON, preserving keys this version does not know, and replace the file atomically;
/// concurrent writers from different daemons are last-write-wins.
/// </summary>
public sealed class UserSettingsStore
{
    /// <summary>Overrides the default location; must be an absolute file path.</summary>
    public const string EnvironmentVariable = "PISTON_SETTINGS_FILE";

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    private readonly Lock _gate = new();

    public UserSettingsStore(string filePath)
    {
        if (!Path.IsPathFullyQualified(filePath))
        {
            throw new ArgumentException($"The settings file path must be absolute; got '{filePath}'.", nameof(filePath));
        }

        FilePath = Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    /// <summary>
    /// Resolves the settings file: <c>$PISTON_SETTINGS_FILE</c> if set, otherwise
    /// <c>Piston/settings.json</c> under the roaming application-data folder
    /// (<c>~/.config</c> on Linux, <c>%APPDATA%</c> on Windows). Null when neither is available.
    /// </summary>
    public static string? ResolveDefaultPath() =>
        ResolveDefaultPath(Environment.GetEnvironmentVariable, Environment.GetFolderPath);

    internal static string? ResolveDefaultPath(
        Func<string, string?> getEnvironmentVariable,
        Func<Environment.SpecialFolder, string> getFolderPath)
    {
        var overridePath = getEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (!Path.IsPathFullyQualified(overridePath))
            {
                throw new InvalidOperationException(
                    $"{EnvironmentVariable} must be an absolute path; got '{overridePath}'.");
            }

            return overridePath;
        }

        var appData = getFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "Piston", "settings.json");
    }

    public UserSettings Get()
    {
        lock (_gate)
        {
            return ToSettings(TryReadObject(out var root) ? root : null);
        }
    }

    /// <summary>Writes the non-null fields of <paramref name="patch"/> and returns the merged settings.</summary>
    /// <exception cref="ArgumentException"><paramref name="patch"/> holds an invalid value; nothing is written.</exception>
    /// <exception cref="InvalidOperationException">The existing file is not a JSON object; it is left untouched.</exception>
    public UserSettings Update(UserSettings patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        patch.Validate();

        lock (_gate)
        {
            if (!TryReadObject(out var root))
            {
                throw new InvalidOperationException(
                    $"Settings file '{FilePath}' is not a valid JSON object; fix or delete it before changing settings.");
            }

            root ??= [];
            var patchNode = JsonSerializer.SerializeToNode(patch, JsonRpcSerializer.Options) as JsonObject ?? [];
            Merge(root, patchNode);
            Write(root);
            return ToSettings(root);
        }
    }

    /// <summary>False only when the file exists but does not hold a JSON object; a missing file gives a null root.</summary>
    private bool TryReadObject(out JsonObject? root)
    {
        root = null;
        string text;
        try
        {
            text = File.ReadAllText(FilePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }

        try
        {
            root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }

        return root is not null;
    }

    private static UserSettings ToSettings(JsonObject? root)
    {
        if (root is null)
        {
            return new UserSettings();
        }

        try
        {
            return root.Deserialize<UserSettings>(JsonRpcSerializer.Options)?.WithoutInvalidValues() ?? new UserSettings();
        }
        catch (JsonException)
        {
            return new UserSettings();
        }
    }

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            if (value is JsonObject patchChild && target[key] is JsonObject targetChild)
            {
                Merge(targetChild, patchChild);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    private void Write(JsonObject root)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        _ = Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = File.Create(tempPath))
            using (var writer = new Utf8JsonWriter(stream, WriterOptions))
            {
                root.WriteTo(writer);
            }

            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch
        {
            File.Delete(tempPath);
            throw;
        }
    }
}
