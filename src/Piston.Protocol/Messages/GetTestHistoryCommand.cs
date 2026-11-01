// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <param name="Test">Fully-qualified name; a multi-targeted test's results under every framework match.</param>
/// <param name="Project">Test project name; null matches any.</param>
public sealed record GetTestHistoryCommand(string Test, string? ProjectId = null);
