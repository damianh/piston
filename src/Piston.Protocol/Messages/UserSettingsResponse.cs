// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <summary>Response to <c>settings/get</c> and <c>settings/update</c>.</summary>
/// <param name="Settings">The effective settings.</param>
/// <param name="FilePath">The settings file the daemon reads and writes.</param>
public sealed record UserSettingsResponse(UserSettings Settings, string FilePath);
