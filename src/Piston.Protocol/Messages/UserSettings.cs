// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <summary>
/// Machine-wide, per-user preferences shared by every Piston daemon and dashboard of the user.
/// Every field is optional: null means "not set, use the built-in default". Also used as the
/// <c>settings/update</c> patch, where only non-null fields are written.
/// </summary>
public sealed record UserSettings(DashboardSettings? Dashboard = null);

/// <summary>Response to <c>settings/get</c> and <c>settings/update</c>.</summary>
/// <param name="Settings">The effective settings.</param>
/// <param name="FilePath">The settings file the daemon reads and writes.</param>
public sealed record UserSettingsResponse(UserSettings Settings, string FilePath);

/// <summary>Web dashboard preferences.</summary>
/// <param name="DetailPane">Where the test details pane is docked when a dashboard opens.</param>
public sealed record DashboardSettings(DetailPanePosition? DetailPane = null);

public enum DetailPanePosition
{
    Right,
    Bottom,
}
