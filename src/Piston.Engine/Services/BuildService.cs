using System.Diagnostics;
using System.Text.RegularExpressions;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

public sealed class BuildService : IBuildService
{
    // MSBuild error/warning format:
    //   path(line,col): error CSXXXX: message [project]
    //   path(line,col): warning CSXXXX: message [project]
    private static readonly Regex ErrorPattern =
        new(@":\s*error\s+\w+\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WarningPattern =
        new(@":\s*warning\s+\w+\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task<BuildResult> BuildAsync(string solutionPath, CancellationToken ct) =>
        BuildAsync(solutionPath, null, ct);

    public async Task<BuildResult> BuildAsync(
        string solutionPath,
        IReadOnlyList<string>? projectPaths,
        CancellationToken ct)
    {
        if (projectPaths is null || projectPaths.Count == 0)
            return await RunBuildAsync($"build \"{solutionPath}\"", ct).ConfigureAwait(false);

        var targets = projectPaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Preferred: build all solution-member targets in one invocation via a temporary
        // solution filter. MSBuild builds the filtered projects in parallel and also builds
        // their ProjectReferences, so referenced libraries are refreshed in test outputs.
        var solution = SolutionFilterBuilder.ReadSolution(solutionPath);
        if (solution is null)
            return await BuildProjectsIndividuallyAsync(targets, ct).ConfigureAwait(false);

        var members = targets.Where(solution.Value.Projects.Contains).ToList();
        var outside = targets.Where(p => !solution.Value.Projects.Contains(p)).ToList();

        var results = new List<BuildResult>();
        if (members.Count > 0)
        {
            var filterPath = SolutionFilterBuilder.WriteTemporaryFilter(solution.Value.SolutionPath, members);
            try
            {
                results.Add(await RunBuildAsync($"build \"{filterPath}\"", ct).ConfigureAwait(false));
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(filterPath)!, recursive: true); } catch { /* best effort */ }
            }
        }

        // Projects outside the solution (e.g. reached only via ProjectReference) cannot be
        // listed in a solution filter, so build them directly.
        if (outside.Count > 0 && !ct.IsCancellationRequested)
            results.Add(await BuildProjectsIndividuallyAsync(outside, ct).ConfigureAwait(false));

        return Aggregate(results, ct.IsCancellationRequested);
    }

    private static async Task<BuildResult> BuildProjectsIndividuallyAsync(
        IReadOnlyList<string> projectPaths,
        CancellationToken ct)
    {
        var results = new List<BuildResult>();
        foreach (var projectPath in projectPaths)
        {
            if (ct.IsCancellationRequested)
                break;

            results.Add(await RunBuildAsync(
                $"build \"{projectPath}\" --no-restore",
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

    private static async Task<BuildResult> RunBuildAsync(string args, CancellationToken ct)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var sw = Stopwatch.StartNew();

        var psi = new ProcessStartInfo("dotnet", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (ErrorPattern.IsMatch(e.Data))
                errors.Add(e.Data.Trim());
            else if (WarningPattern.IsMatch(e.Data))
                warnings.Add(e.Data.Trim());
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                errors.Add(e.Data.Trim());
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            sw.Stop();
            return new BuildResult(BuildStatus.Failed, errors, warnings, sw.Elapsed);
        }

        sw.Stop();

        var status = process.ExitCode == 0 && errors.Count == 0
            ? BuildStatus.Succeeded
            : BuildStatus.Failed;

        return new BuildResult(status, errors, warnings, sw.Elapsed);
    }
}
