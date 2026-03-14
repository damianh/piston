using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class FileWatcherDebounceTests
{
    [Fact]
    public async Task MultipleRapidChanges_FiresOnlyOnce()
    {
        var debounce = TimeSpan.FromMilliseconds(100);
        using var sut = new FileWatcherService(debounce);

        var fired = new List<FileChangeEvent>();
        sut.FileChanged += e => fired.Add(e);

        // Simulate 5 rapid changes via internal ScheduleDebounce indirectly by
        // using reflection to call ScheduleDebounce, or by testing via a temp directory.
        // We test via a real temp directory write since FileWatcherService wraps FSW.
        var dir = Directory.CreateTempSubdirectory("piston-test-");
        try
        {
            sut.Start(dir.FullName);

            // Write the same file multiple times quickly
            var filePath = Path.Combine(dir.FullName, "Test.cs");
            for (var i = 0; i < 5; i++)
            {
                await File.WriteAllTextAsync(filePath, $"// change {i}");
                await Task.Delay(10);
            }

            // Wait for debounce to settle (debounce interval + buffer)
            await Task.Delay(debounce + TimeSpan.FromMilliseconds(150));

            Assert.Single(fired);
        }
        finally
        {
            sut.Stop();
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ExcludedPaths_DoNotFireEvent()
    {
        var debounce = TimeSpan.FromMilliseconds(50);
        using var sut = new FileWatcherService(debounce);

        var fired = new List<FileChangeEvent>();
        sut.FileChanged += e => fired.Add(e);

        var dir = Directory.CreateTempSubdirectory("piston-test-");
        try
        {
            sut.Start(dir.FullName);

            // Write to excluded directories
            var binDir = Directory.CreateDirectory(Path.Combine(dir.FullName, "bin"));
            var objDir = Directory.CreateDirectory(Path.Combine(dir.FullName, "obj"));

            await File.WriteAllTextAsync(Path.Combine(binDir.FullName, "output.cs"), "// bin file");
            await File.WriteAllTextAsync(Path.Combine(objDir.FullName, "output.cs"), "// obj file");

            await Task.Delay(debounce + TimeSpan.FromMilliseconds(100));

            Assert.Empty(fired);
        }
        finally
        {
            sut.Stop();
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task NonWatchedExtension_DoesNotFireEvent()
    {
        var debounce = TimeSpan.FromMilliseconds(50);
        using var sut = new FileWatcherService(debounce);

        var fired = new List<FileChangeEvent>();
        sut.FileChanged += e => fired.Add(e);

        var dir = Directory.CreateTempSubdirectory("piston-test-");
        try
        {
            sut.Start(dir.FullName);

            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "readme.txt"), "hello");
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "data.json"), "{}");

            await Task.Delay(debounce + TimeSpan.FromMilliseconds(100));

            Assert.Empty(fired);
        }
        finally
        {
            sut.Stop();
            dir.Delete(recursive: true);
        }
    }
}
