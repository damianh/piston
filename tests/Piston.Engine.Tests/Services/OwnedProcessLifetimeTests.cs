// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Security;
using Piston.Engine.Tests.Execution;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class OwnedProcessLifetimeTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task OwnerCrash_TerminatesTreeEvenAfterIntermediateParentExits(bool build, bool targetExits)
    {
        var root = Directory.CreateTempSubdirectory("piston-owner-crash-").FullName;
        var children = new List<Process>();
        LeaseHolderProcess? owner = null;
        try
        {
            var fixture = typeof(TestProcessHost.Program).Assembly.Location;
            if (build)
            {
                var project = Path.Combine(root, "Crash.proj");
                await File.WriteAllTextAsync(project, $"""
                    <Project DefaultTargets="Build">
                      <Target Name="Restore" />
                      <Target Name="Build">
                        <Exec Command="dotnet &quot;{SecurityElement.Escape(fixture)}&quot; --spawn-tree &quot;{SecurityElement.Escape(root)}&quot; false" />
                      </Target>
                    </Project>
                    """, _ct);
                owner = LeaseHolderProcess.Start("build", project);
            }
            else
            {
                owner = LeaseHolderProcess.Start(
                    "run-tree", "dotnet", fixture, "--spawn-tree", root, targetExits.ToString());
                var line = owner.WaitForLine("LAUNCHER ");
                children.Add(Process.GetProcessById(int.Parse(line["LAUNCHER ".Length..])));
            }

            var targetId = await ReadPidAsync(Path.Combine(root, "target.pid"));
            if (!targetExits)
            {
                children.Add(Process.GetProcessById(targetId));
            }
            children.Add(Process.GetProcessById(await ReadPidAsync(Path.Combine(root, "descendant.pid"))));
            await WaitForFileAsync(Path.Combine(root, "ready"));
            if (targetExits)
            {
                await children[0].WaitForExitAsync(_ct).WaitAsync(TimeSpan.FromSeconds(10), _ct);
            }

            // Kill only the engine stand-in; tree cleanup must come from containment, not the test.
            owner.Kill();

            foreach (var child in children)
            {
                await child.WaitForExitAsync(_ct).WaitAsync(TimeSpan.FromSeconds(10), _ct);
                Assert.True(child.HasExited);
            }
        }
        finally
        {
            owner?.Dispose();
            foreach (var child in children)
            {
                using (child)
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                        await child.WaitForExitAsync(CancellationToken.None)
                            .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                    }
                }
            }
            await DeleteDirectoryAsync(root);
        }
    }

    // The killed build's dotnet process may still hold its working directory briefly while the job tears down.
    private static async Task DeleteDirectoryAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                await Task.Delay(100, CancellationToken.None);
            }
        }
    }

    private async Task<int> ReadPidAsync(string path)
    {
        await WaitForFileAsync(path);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var text = await File.ReadAllTextAsync(path, timeout.Token);
            if (int.TryParse(text, out var pid))
            {
                return pid;
            }
            await Task.Delay(10, timeout.Token);
        }
    }

    private async Task WaitForFileAsync(string path)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!File.Exists(path))
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}
