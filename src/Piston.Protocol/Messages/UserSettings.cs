// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <summary>
/// Machine-wide, per-user preferences shared by every Piston daemon and dashboard of the user.
/// Every field is optional: null means "not set, use the built-in default". Also used as the
/// <c>settings/update</c> patch, where only non-null fields are written.
/// </summary>
public sealed record UserSettings(DashboardSettings? Dashboard = null)
{
    /// <summary>
    /// Throws when any field holds a value outside its allowed range, such as an undefined enum
    /// value (System.Text.Json accepts any integer for an enum).
    /// </summary>
    /// <exception cref="ArgumentException">A field holds an invalid value.</exception>
    public void Validate() => Dashboard?.Validate();

    /// <summary>Returns a copy with every invalid field reset to null, i.e. to its built-in default.</summary>
    public UserSettings WithoutInvalidValues() => this with { Dashboard = Dashboard?.WithoutInvalidValues() };
}

/// <summary>Response to <c>settings/get</c> and <c>settings/update</c>.</summary>
/// <param name="Settings">The effective settings.</param>
/// <param name="FilePath">The settings file the daemon reads and writes.</param>
public sealed record UserSettingsResponse(UserSettings Settings, string FilePath);

/// <summary>Web dashboard preferences.</summary>
/// <param name="DetailPane">Where the test details pane is docked when a dashboard opens.</param>
public sealed record DashboardSettings(DetailPanePosition? DetailPane = null)
{
    /// <inheritdoc cref="UserSettings.Validate"/>
    public void Validate()
    {
        if (!IsDefined(DetailPane))
        {
            throw new ArgumentException(
                $"Invalid value '{DetailPane}' for dashboard.detailPane.", nameof(DetailPane));
        }
    }

    /// <inheritdoc cref="UserSettings.WithoutInvalidValues"/>
    public DashboardSettings WithoutInvalidValues() =>
        IsDefined(DetailPane) ? this : this with { DetailPane = null };

    private static bool IsDefined<TEnum>(TEnum? value)
        where TEnum : struct, Enum =>
        value is not { } v || Enum.IsDefined(v);
}

public enum DetailPanePosition
{
    Right,
    Bottom,
}
