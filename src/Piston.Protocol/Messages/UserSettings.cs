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
