using System.Text.Json.Serialization;
using Piston.Roslyn.Messages;

namespace Piston.Roslyn;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    WriteIndented = false,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(LoadWorkspaceParams))]
[JsonSerializable(typeof(ShutdownParams))]
[JsonSerializable(typeof(WorkspaceInfo))]
[JsonSerializable(typeof(ProjectInfo))]
[JsonSerializable(typeof(List<ProjectInfo>))]
[JsonSerializable(typeof(bool))]
internal sealed partial class RoslynJsonContext : JsonSerializerContext;
