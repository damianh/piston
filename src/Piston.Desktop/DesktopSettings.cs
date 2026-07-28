using System.Text.Json;

namespace Piston.Desktop;

/// <summary>
/// Persisted desktop app settings, stored in %APPDATA%/Piston/desktop.json
/// (or the XDG/macOS equivalent via <see cref="Environment.SpecialFolder.ApplicationData"/>).
/// </summary>
public sealed class DesktopSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Last solution the user opened; reopened on next launch.</summary>
    public string? LastSolutionPath { get; set; }

    /// <summary>Whether the app registers itself to start on login.</summary>
    public bool AutostartEnabled { get; set; }

    /// <summary>Preferred web dashboard port. 0 = pick a free port.</summary>
    public int WebPort { get; set; }

    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Piston");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "desktop.json");

    public static DesktopSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<DesktopSettings>(json, JsonOptions) ?? new DesktopSettings();
            }
        }
        catch
        {
            // Corrupt settings fall back to defaults.
        }

        return new DesktopSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
