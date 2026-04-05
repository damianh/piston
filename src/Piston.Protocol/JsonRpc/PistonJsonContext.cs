using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Piston.Protocol.Dtos;
using Piston.Protocol.Messages;

namespace Piston.Protocol.JsonRpc;

/// <summary>
/// Compile-time STJ source-generation context for all types that cross the JSON-RPC wire.
/// Replaces reflection-based serialization to eliminate JIT overhead on first use.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy              = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition            = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling                    = JsonNumberHandling.AllowReadingFromString,
    WriteIndented                     = false,
    UseStringEnumConverter            = true)]

// ── Envelope types (internal, promoted from private nested in Task 1) ─────
[JsonSerializable(typeof(RequestEnvelope))]
[JsonSerializable(typeof(ResponseEnvelope))]
[JsonSerializable(typeof(NotificationEnvelope))]
[JsonSerializable(typeof(ErrorEnvelope))]

// ── JsonNode for envelope Params/Result/Data ─────────────────────────────
[JsonSerializable(typeof(JsonNode))]

// ── DTO records ───────────────────────────────────────────────────────────
[JsonSerializable(typeof(TestResultDto))]
[JsonSerializable(typeof(TestSuiteDto))]
[JsonSerializable(typeof(BuildResultDto))]
[JsonSerializable(typeof(BuildStatusDto))]
[JsonSerializable(typeof(PistonPhaseDto))]
[JsonSerializable(typeof(TestStatusDto))]
[JsonSerializable(typeof(CoverageLineDto))]
[JsonSerializable(typeof(FileCoverageDto))]
[JsonSerializable(typeof(ProjectRunStatusDto))]

// ── Message records ───────────────────────────────────────────────────────
[JsonSerializable(typeof(StateSnapshotNotification))]
[JsonSerializable(typeof(PhaseChangedNotification))]
[JsonSerializable(typeof(TestProgressNotification))]
[JsonSerializable(typeof(BuildErrorNotification))]
[JsonSerializable(typeof(FileCoverageUpdatedNotification))]
[JsonSerializable(typeof(StartCommand))]
[JsonSerializable(typeof(ForceRunCommand))]
[JsonSerializable(typeof(StopCommand))]
[JsonSerializable(typeof(SetFilterCommand))]
[JsonSerializable(typeof(ClearResultsCommand))]
[JsonSerializable(typeof(GetFileCoverageCommand))]

// ── Named param records (used by RemoteEngineClient) ─────────────────────
[JsonSerializable(typeof(StartCommandParams))]
[JsonSerializable(typeof(SetFilterCommandParams))]

// ── Concrete collection registrations for IReadOnlyList<T>/IReadOnlyDictionary<K,V> ──
// STJ source generators cannot resolve interface-typed collection properties automatically.
[JsonSerializable(typeof(List<TestSuiteDto>))]
[JsonSerializable(typeof(List<TestResultDto>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<CoverageLineDto>))]
[JsonSerializable(typeof(Dictionary<string, ProjectRunStatusDto>))]

internal sealed partial class PistonJsonContext : JsonSerializerContext
{
}
