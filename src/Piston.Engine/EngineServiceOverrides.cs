// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Services;

namespace Piston.Engine;

/// <summary>Overrides engine services for deterministic tests.</summary>
internal sealed record EngineServiceOverrides(
    IBuildService? BuildService = null,
    IImpactAnalyzer? ImpactAnalyzer = null,
    Func<string, string?>? SdkVersionProvider = null);
