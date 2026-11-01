// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Xunit;

namespace Piston.Tests.Smoke;

/// <summary>Skips unless <c>PISTON_DOGFOOD_SMOKE=1</c>.</summary>
public sealed class DogfoodSmokeFactAttribute : FactAttribute
{
    public DogfoodSmokeFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (Environment.GetEnvironmentVariable("PISTON_DOGFOOD_SMOKE") != "1")
        {
            Skip = "Set PISTON_DOGFOOD_SMOKE=1 to run the real-daemon dogfood smoke (builds tests/fixtures/unit).";
        }
    }
}
