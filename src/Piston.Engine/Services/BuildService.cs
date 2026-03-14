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

        // Build each project individually and aggregate results
        var allErrors = new List<string>();
        var allWarnings = new List<string>();
        var totalDuration = TimeSpan.Zero;
        var overallStatus = BuildStatus.Succeeded;

        foreach (var projectPath in projectPaths)
        {
            if (ct.IsCancellationRequested)
                return new BuildResult(BuildStatus.Failed, allErrors, allWarnings, totalDuration);

            var result = await RunBuildAsync(
                $"build \"{projectPath}\" --no-restore",
                ct).ConfigureAwait(false);

            allErrors.AddRange(result.Errors);
            allWarnings.AddRange(result.Warnings);
            totalDuration += result.Duration;

            if (result.Status == BuildStatus.Failed)
                overallStatus = BuildStatus.Failed;
        }

        return new BuildResult(overallStatus, allErrors, allWarnings, totalDuration);
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
