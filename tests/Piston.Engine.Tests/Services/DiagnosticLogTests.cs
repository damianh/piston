// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class DiagnosticLogTests
{
    [Fact]
    public void Open_DeletesOldRotatedFile_AndWritesStayWithinRotationLimit()
    {
        var root = Directory.CreateTempSubdirectory("piston-diagnostic-log-test-");
        var path = Path.Combine(root.FullName, ".piston", "diagnostics.log");
        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".1", "stale");
            using var log = new DiagnosticLog(root.FullName, maxFileBytes: 1024);
            log.Open();
            Assert.False(File.Exists(path + ".1"));

            for (var i = 0; i < 100; i++)
            {
                log.Write("Test", $"entry-{i:D3} {new string('x', 80)}");
            }

            Assert.True(File.Exists(path + ".1"));
            var current = File.ReadAllText(path);
            var rotated = File.ReadAllText(path + ".1");
            Assert.Contains("entry-099", current, StringComparison.Ordinal);
            Assert.Contains("Rotated diagnostics.log", current, StringComparison.Ordinal);
            Assert.DoesNotContain("entry-099", rotated, StringComparison.Ordinal);
            Assert.InRange(new FileInfo(path).Length, 1, 1024);
            Assert.InRange(new FileInfo(path + ".1").Length, 1, 1024);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
