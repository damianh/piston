// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <summary>
/// Machine-wide, per-user preferences shared by every Piston daemon and dashboard of the user.
/// Every field is optional: null means "not set, use the built-in default". Used both as the
/// <c>settings/get</c> response and as the <c>settings/update</c> patch, where only non-null
/// fields are written.
/// </summary>
public sealed record UserSettings(DashboardSettings? Dashboard = null);

/// <summary>Web dashboard preferences.</summary>
/// <param name="DetailPane">Where the test details pane is docked when a dashboard opens.</param>
public sealed record DashboardSettings(DetailPanePosition? DetailPane = null);

public enum DetailPanePosition
{
    Right,
    Bottom,
}
