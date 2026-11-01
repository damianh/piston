// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

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
