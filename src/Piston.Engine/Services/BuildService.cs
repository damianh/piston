// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

public sealed class BuildService : IBuildService
{
    private readonly MtpWarmHostPool? _warmHosts;

    public BuildService()
    {
    }

    internal BuildService(MtpWarmHostPool? warmHosts) => _warmHosts = warmHosts;

    public Task<BuildResult> BuildAsync(string solutionPath, CancellationToken ct) =>
        BuildAsync(solutionPath, null, null, ct);

    public Task<BuildResult> BuildAsync(
        string solutionPath,
        IReadOnlyList<string>? projectPaths,
        CancellationToken ct) =>
        BuildAsync(solutionPath, projectPaths, null, ct);

    public async Task<BuildResult> BuildAsync(
        string solutionPath,
        IReadOnlyList<string>? projectPaths,
        Action<string>? onProjectBuilt,
        CancellationToken ct)
    {
        if (_warmHosts is not null)
        {
            await _warmHosts.RetireBeforeBuildAsync().ConfigureAwait(false);
        }

        if (projectPaths is null || projectPaths.Count == 0)
        {
            // Absolute, since the build starts in the solution's directory: a relative path with a
            // directory (sub/My.slnx) would otherwise resolve against it a second time.
            var fullSolutionPath = Path.GetFullPath(solutionPath);
            return await RunBuildAsync($"build \"{fullSolutionPath}\"", TestProcessRunner.WorkingDirectoryFor(fullSolutionPath), onProjectBuilt, ct).ConfigureAwait(false);
        }

        var targets = projectPaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Preferred: build all solution-member targets in one invocation via a temporary
        // solution filter. MSBuild builds the filtered projects in parallel and also builds
        // their ProjectReferences, so referenced libraries are refreshed in test outputs.
        var solution = SolutionFilterBuilder.ReadSolution(solutionPath);
        if (solution is null)
        {
            return await BuildProjectsIndividuallyAsync(targets, onProjectBuilt, ct).ConfigureAwait(false);
        }

        var members = targets.Where(solution.Value.Projects.Contains).ToList();
        var outside = targets.Where(p => !solution.Value.Projects.Contains(p)).ToList();

        var results = new List<BuildResult>();
        if (members.Count > 0)
        {
            var filterPath = SolutionFilterBuilder.WriteTemporaryFilter(solution.Value.SolutionPath, members);
            try
            {
                results.Add(await RunBuildAsync($"build \"{filterPath}\"", TestProcessRunner.WorkingDirectoryFor(solution.Value.SolutionPath), onProjectBuilt, ct).ConfigureAwait(false));
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(filterPath)!, recursive: true); } catch { /* best effort */ }
            }
        }

        // Projects outside the solution (e.g. reached only via ProjectReference) cannot be
        // listed in a solution filter, so build them directly.
        if (outside.Count > 0 && !ct.IsCancellationRequested)
        {
            results.Add(await BuildProjectsIndividuallyAsync(outside, onProjectBuilt, ct).ConfigureAwait(false));
        }

        return Aggregate(results, ct.IsCancellationRequested);
    }

    private static async Task<BuildResult> BuildProjectsIndividuallyAsync(
        IReadOnlyList<string> projectPaths,
        Action<string>? onProjectBuilt,
        CancellationToken ct)
    {
        var results = new List<BuildResult>();
        foreach (var projectPath in projectPaths)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            results.Add(await RunBuildAsync(
                $"build \"{projectPath}\" --no-restore",
                TestProcessRunner.WorkingDirectoryFor(projectPath),
                onProjectBuilt,
                ct).ConfigureAwait(false));
        }

        return Aggregate(results, ct.IsCancellationRequested);
    }

    private static BuildResult Aggregate(IReadOnlyList<BuildResult> results, bool cancelled)
    {
        var failed = cancelled || results.Any(r => r.Status == BuildStatus.Failed);
        return new BuildResult(
            failed ? BuildStatus.Failed : BuildStatus.Succeeded,
            results.SelectMany(r => r.Errors).ToList(),
            results.SelectMany(r => r.Warnings).ToList(),
            results.Aggregate(TimeSpan.Zero, (sum, r) => sum + r.Duration));
    }

    /// <param name="workingDirectory">
    /// Where <c>dotnet build</c> starts: the target's directory, so the SDK chosen by its <c>global.json</c> applies
    /// rather than the one for Piston's own directory.
    /// </param>
    private static async Task<BuildResult> RunBuildAsync(
        string args, string? workingDirectory, Action<string>? onProjectBuilt, CancellationToken ct)
    {
        var output = new BuildOutputCollector();
        var sw = Stopwatch.StartNew();

        var psi = new ProcessStartInfo("dotnet", args)
        {
            WorkingDirectory = workingDirectory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Reused MSBuild nodes can retain the redirected pipes after dotnet exits,
        // leaving WaitForExitAsync waiting indefinitely for output EOF.
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        _ = ChildProcessEnvironment.Prepare(psi);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            output.AddOutputLine(e.Data);
            if (onProjectBuilt is not null && BuildOutputCollector.TryParseBuiltProject(e.Data, out var projectName))
            {
                onProjectBuilt(projectName);
            }
        };
        process.ErrorDataReceived += (_, e) => output.AddErrorLine(e.Data);

        _ = process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            await ProcessTermination.KillAndWaitAsync(process,
                message => DiagnosticLog.Instance?.Write("BuildService", message)).ConfigureAwait(false);
            sw.Stop();
            return output.ToCancelledResult(sw.Elapsed);
        }

        sw.Stop();

        var result = output.ToResult(process.ExitCode, sw.Elapsed);
        if (result.Status == BuildStatus.Failed)
        {
            DiagnosticLog.Instance?.Write("BuildService",
                $"dotnet {args} failed (exit code {process.ExitCode}, {result.Errors.Count} error(s)). " +
                $"First error: {result.Errors[0]}");
        }

        return result;
    }
}
