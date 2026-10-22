// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>A test that is running now.</summary>
/// <param name="Name">The test's result key (fully qualified name, with the framework when known).</param>
/// <param name="ProjectPath">The test project running it.</param>
/// <param name="StartedAt">When Piston first saw it running.</param>
/// <param name="PossiblyHanging">True once it has run longer than the hang threshold.</param>
public sealed record RunningTest(string Name, string ProjectPath, DateTimeOffset StartedAt, bool PossiblyHanging);

/// <summary>The test that made its project's run time out.</summary>
/// <param name="Test">The test's result key.</param>
/// <param name="Limit">The test timeout it exceeded.</param>
public sealed record TestTimeout(string Test, TimeSpan Limit);
