// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

/// <summary>
/// Drives <see cref="MtpWarmHostPool"/> against <c>Piston.TestProcessHost</c>, which acts as a minimal
/// MTP server when started as a warm host. Each reported test uid is <c>&lt;pid&gt;:&lt;request&gt;</c>.
/// </summary>
public sealed class MtpWarmHostPoolTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _appDirectory = Directory.CreateTempSubdirectory("piston-warm-").FullName;
    private readonly string _appPath;

    public MtpWarmHostPoolTests()
    {
        var source = typeof(TestProcessHost.Program).Assembly.Location;
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + ".*"))
        {
            File.Copy(file, Path.Combine(_appDirectory, Path.GetFileName(file)));
        }

        _appPath = Path.Combine(_appDirectory, Path.GetFileName(source));
    }

    public void Dispose()
    {
        try { Directory.Delete(_appDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task RepeatedRequests_ReuseOneHost()
    {
        await using var pool = new MtpWarmHostPool();

        var discover = await RunAsync(pool, MtpServerSession.DiscoverMethod);
        var run = await RunAsync(pool, MtpServerSession.RunMethod);
        var again = await RunAsync(pool, MtpServerSession.RunMethod);

        Assert.Equal(MtpServerSession.Outcome.Completed, discover.Outcome);
        Assert.Equal(MtpServerSession.Outcome.Completed, again.Outcome);
        Assert.Equal(discover.Pid, run.Pid);
        Assert.Equal(discover.Pid, again.Pid);
        Assert.Equal([1, 2, 3], new[] { discover.Request, run.Request, again.Request });
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public async Task ChangedOutput_ReplacesTheHost()
    {
        await using var pool = new MtpWarmHostPool();

        var first = await RunAsync(pool, MtpServerSession.RunMethod);
        await File.WriteAllTextAsync(Path.Combine(_appDirectory, "rebuilt.txt"), "x", _ct);
        var second = await RunAsync(pool, MtpServerSession.RunMethod);

        Assert.NotEqual(first.Pid, second.Pid);
        Assert.Equal(1, second.Request);
        Assert.True(HasExited(first.Pid));
    }

    [Fact]
    public async Task Dispose_EndsIdleHosts()
    {
        var pool = new MtpWarmHostPool();
        var run = await RunAsync(pool, MtpServerSession.RunMethod);

        await pool.DisposeAsync();

        Assert.Equal(0, pool.Count);
        Assert.True(HasExited(run.Pid));
    }

    [Fact]
    public async Task AfterDispose_RequestsRunCold()
    {
        var pool = new MtpWarmHostPool();
        await pool.DisposeAsync();

        // Without PISTON_WARM_HOST the test host fails before connecting.
        var result = await pool.RunAsync(_appPath, _appDirectory, MtpServerSession.RunMethod, null, _ => { }, _ct);

        Assert.Equal(MtpServerSession.Outcome.NotStarted, result.Outcome);
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void OutputFingerprint_ChangesWithTheBuildOutput()
    {
        var before = MtpWarmHostPool.OutputFingerprint(_appPath);
        Assert.Equal(before, MtpWarmHostPool.OutputFingerprint(_appPath));

        File.SetLastWriteTimeUtc(_appPath, File.GetLastWriteTimeUtc(_appPath).AddSeconds(5));
        var touched = MtpWarmHostPool.OutputFingerprint(_appPath);
        Assert.NotEqual(before, touched);

        File.WriteAllText(Path.Combine(_appDirectory, "extra.dll"), "x");
        Assert.NotEqual(touched, MtpWarmHostPool.OutputFingerprint(_appPath));
    }

    private async Task<(MtpServerSession.Outcome Outcome, int Pid, int Request)> RunAsync(MtpWarmHostPool pool, string method)
    {
        string? uid = null;
        var result = await pool.RunAsync(_appPath, _appDirectory, method, null,
            nodes => uid = nodes[0].GetProperty("uid").GetString(), _ct).WaitAsync(TimeSpan.FromSeconds(60), _ct);
        Assert.True(uid is not null, $"{result.Outcome}: {result.Error} {string.Join(" | ", result.Stderr)}");
        var parts = uid.Split(':');
        return (result.Outcome, int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    private static bool HasExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
