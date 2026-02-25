using System.Diagnostics;
using Piston.Core.Models;

namespace Piston.Core.Services;

public sealed class TestRunnerService : ITestRunnerService
{
    private readonly ITestResultParser _parser;

    public TestRunnerService(ITestResultParser parser)
    {
        _parser = parser;
    }

    public async Task<IReadOnlyList<TestSuite>> RunTestsAsync(string solutionPath, CancellationToken ct)
    {
        var resultsDir = Path.Combine(Path.GetTempPath(), $"piston-{Guid.NewGuid():N}");
        Directory.CreateDirectory(resultsDir);

        try
        {
            var args = $"test \"{solutionPath}\" --no-build " +
                       $"--logger \"trx;LogFileName=piston-results.trx\" " +
                       $"--results-directory \"{resultsDir}\"";

            var psi = new ProcessStartInfo("dotnet", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi };
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
                return [];
            }

            // dotnet test writes one TRX per test project under resultsDir
            var trxFiles = Directory.GetFiles(resultsDir, "*.trx", SearchOption.AllDirectories);

            var suites = new List<TestSuite>();
            foreach (var trx in trxFiles)
            {
                try
                {
                    suites.AddRange(_parser.Parse(trx));
                }
                catch
                {
                    // Malformed TRX — skip rather than crash
                }
            }

            return suites;
        }
        finally
        {
            // Best-effort cleanup of temp results directory
            try { Directory.Delete(resultsDir, recursive: true); } catch { /* ignore */ }
        }
    }
}
