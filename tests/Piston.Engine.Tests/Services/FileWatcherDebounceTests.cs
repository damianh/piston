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

        var fired = new List<FileChangeBatch>();
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
    public async Task MultipleDistinctFiles_BatchContainsAllUniqueFiles()
    {
        var debounce = TimeSpan.FromMilliseconds(150);
        using var sut = new FileWatcherService(debounce);

        var batches = new List<FileChangeBatch>();
        sut.FileChanged += b => batches.Add(b);

        var dir = Directory.CreateTempSubdirectory("piston-test-");
        try
        {
            sut.Start(dir.FullName);

            // Write multiple distinct files quickly
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Alpha.cs"), "// alpha");
            await Task.Delay(10);
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Beta.cs"), "// beta");
            await Task.Delay(10);
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Gamma.cs"), "// gamma");

            // Wait for debounce to settle
            await Task.Delay(debounce + TimeSpan.FromMilliseconds(200));

            Assert.Single(batches);
            var batch = batches[0];
            var paths = batch.Changes.Select(c => Path.GetFileName(c.FilePath)).ToList();
            Assert.Contains("Alpha.cs", paths);
            Assert.Contains("Beta.cs", paths);
            Assert.Contains("Gamma.cs", paths);
        }
        finally
        {
            sut.Stop();
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SameFileChangedMultipleTimes_BatchContainsOnlyOneEntry()
    {
        var debounce = TimeSpan.FromMilliseconds(150);
        using var sut = new FileWatcherService(debounce);

        var batches = new List<FileChangeBatch>();
        sut.FileChanged += b => batches.Add(b);

        var dir = Directory.CreateTempSubdirectory("piston-test-");
        try
        {
            sut.Start(dir.FullName);

            var filePath = Path.Combine(dir.FullName, "Repeated.cs");
            // Write the same file 3 times
            await File.WriteAllTextAsync(filePath, "// version 1");
            await Task.Delay(10);
            await File.WriteAllTextAsync(filePath, "// version 2");
            await Task.Delay(10);
            await File.WriteAllTextAsync(filePath, "// version 3");

            // Wait for debounce to settle
            await Task.Delay(debounce + TimeSpan.FromMilliseconds(200));

            Assert.Single(batches);
            var batch = batches[0];
            // Same file path → only one entry
            var paths = batch.Changes.Select(c => c.FilePath).ToList();
            Assert.Single(paths);
            Assert.EndsWith("Repeated.cs", paths[0], StringComparison.OrdinalIgnoreCase);
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

        var fired = new List<FileChangeBatch>();
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

        var fired = new List<FileChangeBatch>();
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
