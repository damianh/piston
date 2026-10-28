// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json.Serialization;
using Piston.Engine.Execution;

namespace Piston.Hosting;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DashboardEndpoint))]
[JsonSerializable(typeof(ExecutionOwnerInfo))]
internal partial class DashboardJsonContext : JsonSerializerContext;
