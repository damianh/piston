using System.Diagnostics;
using System.Globalization;

namespace Piston.Measurements;

public sealed record CommandResult(string[] Command, int RootPid, int ExitCode, bool TimedOut,
    double ElapsedMs, long StartedWallNs, string Log,
    double CpuSecondsObservedLowerBound, long PeakRssBytesSampled);

public sealed record ResourceSample(long ElapsedNs, double CpuSecondsAlive, long RssBytesAlive);

public static class Processes
{
    public static List<int> OwnedPids(int pid)
    {
        var result = new List<int> { pid };
        var seen = new HashSet<int> { pid };
        for (var index = 0; index < result.Count; index++)
        {
            var current = result[index];
            try
            {
                foreach (var child in File.ReadAllText($"/proc/{current}/task/{current}/children")
                             .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    var id = int.Parse(child, CultureInfo.InvariantCulture);
                    if (seen.Add(id)) result.Add(id);
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return result;
    }

    public static ResourceSample SampleOwned(int pid, long elapsedNs)
    {
        double cpu = 0;
        long rss = 0;
        foreach (var child in OwnedPids(pid))
        {
            try
            {
                using var process = Process.GetProcessById(child);
                cpu += process.TotalProcessorTime.TotalSeconds;
                rss += process.WorkingSet64;
            }
            catch (ArgumentException) { } // A sampled descendant has already exited.
            catch (InvalidOperationException) { }
        }
        return new(elapsedNs, cpu, rss);
    }

    public static void TerminateOwned(Process process)
    {
        foreach (var pid in OwnedPids(process.Id).AsEnumerable().Reverse())
        {
            try
            {
                using var owned = Process.GetProcessById(pid);
                owned.Kill();
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        process.WaitForExit();
    }

    public static async Task<CommandResult> Command(string[] argv, string cwd, string artifacts,
        string label, double timeout, Dictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var start = Evidence.NowNs;
        var wall = Evidence.WallNs;
        var info = StartInfo(argv, cwd, environment);
        using var process = new Process { StartInfo = info };
        await using var log = new StreamWriter(Path.Combine(artifacts, label + ".log"));
        using var logLock = new SemaphoreSlim(1);
        process.Start();
        var stdout = CopyLog(process.StandardOutput, log, logLock);
        var stderr = CopyLog(process.StandardError, log, logLock);
        var samples = new List<ResourceSample>();
        var timedOut = false;
        try
        {
            while (!process.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                samples.Add(SampleOwned(process.Id, Evidence.NowNs - start));
                if ((Evidence.NowNs - start) / 1e9 > timeout)
                {
                    timedOut = true;
                    TerminateOwned(process);
                    break;
                }
                await Task.Delay(50, cancellationToken);
            }
        }
        finally
        {
            if (!process.HasExited) TerminateOwned(process);
            await Task.WhenAll(stdout, stderr);
        }
        Evidence.WriteJson(Path.Combine(artifacts, label + ".resources.json"), samples);
        return new(argv, process.Id, process.ExitCode, timedOut, (Evidence.NowNs - start) / 1e6,
            wall, label + ".log", samples.Count > 0 ? samples.Max(sample => sample.CpuSecondsAlive) : 0,
            samples.Count > 0 ? samples.Max(sample => sample.RssBytesAlive) : 0);
    }

    private static async Task CopyLog(StreamReader source, StreamWriter log, SemaphoreSlim gate)
    {
        while (await source.ReadLineAsync() is { } line)
        {
            await gate.WaitAsync();
            try { await log.WriteLineAsync(line); }
            finally { gate.Release(); }
        }
    }

    private static ProcessStartInfo StartInfo(string[] argv, string cwd, Dictionary<string, string>? environment)
    {
        var info = new ProcessStartInfo(argv[0])
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in argv.Skip(1)) info.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        return info;
    }

    public static async Task<string> Capture(string[] argv, string cwd)
    {
        using var process = new Process { StartInfo = StartInfo(argv, cwd, null) };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidDataException($"{argv[0]} failed: {error.Trim()}");
        return output.Trim();
    }

    public static string[] TestCommand(string solution, string results) =>
        ["dotnet", "test", solution, "--no-restore", "--logger", "trx",
            "--results-directory", results, "--nologo", "--verbosity", "quiet"];
}
