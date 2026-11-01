// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>The test that made its project's run time out.</summary>
/// <param name="Test">The test's result key.</param>
/// <param name="Limit">The test timeout it exceeded.</param>
public sealed record TestTimeout(string Test, TimeSpan Limit);
