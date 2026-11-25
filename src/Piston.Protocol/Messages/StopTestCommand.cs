// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <summary>Parameters for <c>engine/stopTest</c>.</summary>
/// <param name="ProjectPath">
/// The test project to stop; null stops the project of the longest-running test.
/// </param>
public sealed record StopTestCommand(string? ProjectPath = null);

/// <summary>Response to <c>engine/stopTest</c>.</summary>
/// <param name="Stopped">Whether a running test project was found and stopped.</param>
/// <param name="ProjectPath">The stopped test project.</param>
/// <param name="TestName">The running test that was failed, when the project had one.</param>
public sealed record StopTestResponse(bool Stopped, string? ProjectPath = null, string? TestName = null);
