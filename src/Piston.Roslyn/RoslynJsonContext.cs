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
[JsonSerializable(typeof(GetDiagnosticsParams))]
[JsonSerializable(typeof(SemanticSearchParams))]
[JsonSerializable(typeof(GetAstParams))]
[JsonSerializable(typeof(DiagnosticResult))]
[JsonSerializable(typeof(DiagnosticsResponse))]
[JsonSerializable(typeof(List<DiagnosticResult>))]
[JsonSerializable(typeof(SymbolReference))]
[JsonSerializable(typeof(SemanticSearchResponse))]
[JsonSerializable(typeof(List<SymbolReference>))]
[JsonSerializable(typeof(AstNode))]
[JsonSerializable(typeof(AstResponse))]
[JsonSerializable(typeof(List<AstNode>))]
[JsonSerializable(typeof(RenameParams))]
[JsonSerializable(typeof(FileChangedParams))]
[JsonSerializable(typeof(FileChange))]
[JsonSerializable(typeof(RenameResponse))]
[JsonSerializable(typeof(List<FileChange>))]
internal sealed partial class RoslynJsonContext : JsonSerializerContext;
