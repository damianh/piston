// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json.Nodes;
using Piston.Hosting.Configuration;
using Piston.Hosting.Protocol;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;
using Xunit;

namespace Piston.Tests.Hosting;

public sealed class UserSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"piston-settings-{Guid.NewGuid():N}");

    private string SettingsPath => Path.Combine(_directory, "nested", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Get_MissingFile_ReturnsDefaults()
    {
        var store = new UserSettingsStore(SettingsPath);

        Assert.Equal(new UserSettings(), store.Get());
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void Update_CreatesFileAndRoundTrips()
    {
        var store = new UserSettingsStore(SettingsPath);

        var merged = store.Update(new UserSettings(new DashboardSettings(DetailPanePosition.Bottom)));

        Assert.Equal(DetailPanePosition.Bottom, merged.Dashboard?.DetailPane);
        Assert.Equal(DetailPanePosition.Bottom, new UserSettingsStore(SettingsPath).Get().Dashboard?.DetailPane);
        var json = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal("bottom", json["dashboard"]?["detailPane"]?.GetValue<string>());
    }

    [Fact]
    public void Update_PreservesUnknownKeysAndUnsetFields()
    {
        Write("""
            {
              // hand-written
              "future": { "keep": true },
              "dashboard": { "detailPane": "bottom", "theme": "dark" },
            }
            """);
        var store = new UserSettingsStore(SettingsPath);

        _ = store.Update(new UserSettings(new DashboardSettings(DetailPanePosition.Right)));
        _ = store.Update(new UserSettings());

        var json = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.True(json["future"]?["keep"]?.GetValue<bool>());
        Assert.Equal("dark", json["dashboard"]?["theme"]?.GetValue<string>());
        Assert.Equal("right", json["dashboard"]?["detailPane"]?.GetValue<string>());
    }

    [Fact]
    public void Get_InvalidJsonOrValues_ReturnsDefaults()
    {
        var store = new UserSettingsStore(SettingsPath);

        Write("{ not json");
        Assert.Equal(new UserSettings(), store.Get());

        Write("""{ "dashboard": { "detailPane": "left" } }""");
        Assert.Equal(new UserSettings(), store.Get());
    }

    [Fact]
    public void Update_InvalidJson_ThrowsAndLeavesFileUntouched()
    {
        Write("{ not json");
        var store = new UserSettingsStore(SettingsPath);

        _ = Assert.Throws<InvalidOperationException>(() =>
            store.Update(new UserSettings(new DashboardSettings(DetailPanePosition.Bottom))));
        Assert.Equal("{ not json", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Update_InvalidValueInFile_IsReplaced()
    {
        Write("""{ "dashboard": { "detailPane": "left" } }""");
        var store = new UserSettingsStore(SettingsPath);

        var merged = store.Update(new UserSettings(new DashboardSettings(DetailPanePosition.Bottom)));

        Assert.Equal(DetailPanePosition.Bottom, merged.Dashboard?.DetailPane);
    }

    [Fact]
    public void Constructor_RelativePath_Throws() =>
        Assert.Throws<ArgumentException>(() => new UserSettingsStore("settings.json"));

    [Fact]
    public void ResolveDefaultPath_UsesEnvironmentOverride()
    {
        var overridePath = Path.Combine(_directory, "custom.json");

        var path = UserSettingsStore.ResolveDefaultPath(
            name => name == UserSettingsStore.EnvironmentVariable ? overridePath : null,
            _ => throw new InvalidOperationException("Folder lookup should not be used."));

        Assert.Equal(overridePath, path);
    }

    [Fact]
    public void ResolveDefaultPath_RelativeOverride_Throws() =>
        Assert.Throws<InvalidOperationException>(() => UserSettingsStore.ResolveDefaultPath(
            _ => "relative/settings.json",
            _ => _directory));

    [Fact]
    public void ResolveDefaultPath_FallsBackToApplicationData()
    {
        Environment.SpecialFolder? requested = null;

        var path = UserSettingsStore.ResolveDefaultPath(_ => null, folder =>
        {
            requested = folder;
            return _directory;
        });

        Assert.Equal(Environment.SpecialFolder.ApplicationData, requested);
        Assert.Equal(Path.Combine(_directory, "Piston", "settings.json"), path);
        Assert.Null(UserSettingsStore.ResolveDefaultPath(_ => null, _ => string.Empty));
    }

    [Fact]
    public async Task Dispatcher_GetAndUpdateSettings()
    {
        var dispatcher = new EngineCommandDispatcher(null!);
        dispatcher.SetUserSettingsStore(new UserSettingsStore(SettingsPath));

        var initial = await dispatcher.HandleCommandAsync(ProtocolMethods.SettingsGet, null, CancellationToken.None);
        var updated = await dispatcher.HandleCommandAsync(
            ProtocolMethods.SettingsUpdate,
            JsonNode.Parse("""{ "dashboard": { "detailPane": "bottom" } }"""),
            CancellationToken.None);

        Assert.Equal("{}", initial?["settings"]?.ToJsonString());
        Assert.Equal(SettingsPath, initial?["filePath"]?.GetValue<string>());
        Assert.Equal("bottom", updated?["settings"]?["dashboard"]?["detailPane"]?.GetValue<string>());
    }

    [Fact]
    public async Task Dispatcher_InvalidSettings_IsInvalidParams()
    {
        var dispatcher = new EngineCommandDispatcher(null!);
        dispatcher.SetUserSettingsStore(new UserSettingsStore(SettingsPath));

        var ex = await Assert.ThrowsAsync<JsonRpcException>(() => dispatcher.HandleCommandAsync(
            ProtocolMethods.SettingsUpdate,
            JsonNode.Parse("""{ "dashboard": { "detailPane": "left" } }"""),
            CancellationToken.None));

        Assert.Equal(JsonRpcErrorCodes.InvalidParams, ex.Code);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public async Task Dispatcher_WithoutStore_IsMethodNotFound()
    {
        var dispatcher = new EngineCommandDispatcher(null!);

        var ex = await Assert.ThrowsAsync<JsonRpcException>(() =>
            dispatcher.HandleCommandAsync(ProtocolMethods.SettingsGet, null, CancellationToken.None));

        Assert.Equal(JsonRpcErrorCodes.MethodNotFound, ex.Code);
    }

    private void Write(string content)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, content);
    }
}
