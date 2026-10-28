// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>Tests a user asked to run, for example from a node in the dashboard's test tree.</summary>
/// <param name="Projects">Names of the test projects to run, as reported in <see cref="TestSuite.Name"/>.</param>
/// <param name="Tests">
/// Fully qualified names (<see cref="TestResult.FullyQualifiedName"/>) of the tests to run within those projects;
/// null runs every test in them. A parameterised test's arguments are ignored, so all of its cases run. Names may
/// not contain double quotes.
/// </param>
/// <param name="ProjectIds">Solution-relative project IDs of the test projects to run.</param>
/// <param name="Tests">Fully-qualified test names to run, or null to run every test in those projects.</param>
public sealed record TestSelection(IReadOnlyList<string> ProjectIds, IReadOnlyList<string>? Tests = null);
