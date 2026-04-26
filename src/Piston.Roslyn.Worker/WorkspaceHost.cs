using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis.MSBuild;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Transports;
using Piston.Roslyn;
using Piston.Roslyn.Messages;

namespace Piston.Roslyn.Worker;

internal static class WorkspaceHost
{
    public static async Task RunAsync(Stream input, Stream output, CancellationToken ct)
    {
        var stream = new StdioDuplexStream(input, output);
        var writeLock = new SemaphoreSlim(1, 1);
        MSBuildWorkspace? workspace = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var data = await MessageFramer.ReadMessageAsync(stream, ct).ConfigureAwait(false);
                if (data is null)
                {
                    break; // EOF
                }

                var message = JsonRpcSerializer.DeserializeMessage(data.Value);
                if (message is not JsonRpcRequest request)
                {
                    continue;
                }

                JsonRpcResponse response;
                try
                {
                    var result = request.Method switch
                    {
                        RoslynMethods.LoadWorkspace => await HandleLoadWorkspace(
                            request.Params, workspace, ct).ConfigureAwait(false),
                        RoslynMethods.Shutdown => HandleShutdown(),
                        _ => throw new NotSupportedException($"Unknown method: {request.Method}"),
                    };

                    response = new JsonRpcResponse(request.Id, result);
                }
                catch (Exception ex)
                {
                    response = new JsonRpcResponse(
                        request.Id,
                        Error: new JsonRpcError(JsonRpcErrorCodes.InternalError, ex.Message));
                }

                var bytes = JsonRpcSerializer.Serialize(response);
                await writeLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await MessageFramer.WriteMessageAsync(stream, bytes, ct).ConfigureAwait(false);
                }
                finally
                {
                    writeLock.Release();
                }

                if (request.Method == RoslynMethods.Shutdown)
                {
                    break;
                }
            }
        }
        finally
        {
            workspace?.Dispose();
            writeLock.Dispose();
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<JsonNode?> HandleLoadWorkspace(
        JsonNode? @params, MSBuildWorkspace? existingWorkspace, CancellationToken ct)
    {
        var loadParams = JsonSerializer.Deserialize(
            @params, RoslynJsonContext.Default.LoadWorkspaceParams)
            ?? throw new ArgumentException("Missing load workspace parameters.");

        var path = loadParams.Path;

        existingWorkspace?.Dispose();
        var workspace = MSBuildWorkspace.Create();

        Microsoft.CodeAnalysis.Solution solution;
        if (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            solution = await workspace.OpenSolutionAsync(path, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        else if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var project = await workspace.OpenProjectAsync(path, cancellationToken: ct)
                .ConfigureAwait(false);
            solution = project.Solution;
        }
        else
        {
            throw new ArgumentException($"Unsupported file type: {path}. Expected .sln or .csproj.");
        }

        var projects = solution.Projects.Select(p => new ProjectInfo(
            p.Name,
            p.FilePath ?? string.Empty,
            p.Documents.Count())).ToList();

        var info = new WorkspaceInfo(path, projects);
        return JsonSerializer.SerializeToNode(info, RoslynJsonContext.Default.WorkspaceInfo);
    }

    private static JsonNode? HandleShutdown()
    {
        return JsonSerializer.SerializeToNode(true, RoslynJsonContext.Default.Boolean);
    }
}
