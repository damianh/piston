// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class TestArtifactStoreTests
{
    private const string RunId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Retain_CopiesAttachmentAndResolveReturnsOnlyTheRetainedFile()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "source.txt");
            File.WriteAllText(source, "test artifact");
            var store = new TestArtifactStore(root);

            var retained = Retain(store, RunId,
            [
                new TestAttachment(source, "report.txt"),
                new TestAttachment(Path.GetFileName(source), "relative report.txt"),
            ]);
            var attachments = Assert.Single(retained).Tests[0].Attachments;
            var attachment = attachments[0];

            Assert.True(attachment.Retained);
            Assert.Equal(RunId, attachment.RunId);
            Assert.Equal(0, attachment.Index);
            Assert.Equal("test artifact", File.ReadAllText(store.Resolve(attachment)!));
            Assert.Null(store.Resolve(attachment with { Path = "../source.txt" }));
            Assert.True(attachments[1].Retained);
            Assert.Equal("test artifact", File.ReadAllText(store.Resolve(attachments[1])!));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Retain_ReportsMissingAndOverLimitAttachments()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "large.bin");
            using (var file = new FileStream(source, FileMode.CreateNew))
            {
                file.SetLength(TestArtifactStore.MaxFileBytes + 1);
            }

            var attachments = new List<TestAttachment>
            {
                new(Path.Combine(root, "missing.txt")),
                new(source),
            };
            attachments.AddRange(Enumerable.Range(0, TestArtifactStore.MaxAttachmentsPerTest + 1)
                .Select(index =>
                {
                    var path = Path.Combine(root, $"small-{index}.txt");
                    File.WriteAllText(path, "x");
                    return new TestAttachment(path);
                }));

            var retained = Retain(new TestArtifactStore(root), RunId, attachments);
            var results = Assert.Single(retained).Tests[0].Attachments;

            Assert.False(results[0].Retained);
            Assert.Contains("missing", results[0].NotRetainedReason, StringComparison.OrdinalIgnoreCase);
            Assert.False(results[1].Retained);
            Assert.Contains("per-file limit", results[1].NotRetainedReason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(TestArtifactStore.MaxAttachmentsPerTest, results.Count(attachment => attachment.Retained));
            Assert.Contains("Per-test limit", results[^1].NotRetainedReason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Prune_RemovesUnreferencedFilesAndRunsAndClearRemovesTheStore()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "source.txt");
            File.WriteAllText(source, "keep");
            var store = new TestArtifactStore(root);
            var retained = Retain(store, RunId, [new TestAttachment(source)]);
            var attachment = Assert.Single(Assert.Single(retained).Tests[0].Attachments);
            var runDirectory = Path.Combine(root, ".piston", "artifacts", RunId);
            var unreferenced = Path.Combine(runDirectory, "9999-unreferenced.txt");
            File.WriteAllText(unreferenced, "remove");
            var obsoleteRun = Path.Combine(root, ".piston", "artifacts", "fedcba9876543210fedcba9876543210");
            Directory.CreateDirectory(obsoleteRun);
            File.WriteAllText(Path.Combine(obsoleteRun, "0000-obsolete.txt"), "remove");

            store.Prune([], retained);

            Assert.True(File.Exists(store.Resolve(attachment)!));
            Assert.False(File.Exists(unreferenced));
            Assert.False(Directory.Exists(obsoleteRun));

            store.Clear();

            Assert.False(Directory.Exists(Path.Combine(root, ".piston", "artifacts")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IReadOnlyList<TestSuite> Retain(
        TestArtifactStore store,
        string runId,
        IReadOnlyList<TestAttachment> attachments) =>
        store.Retain(runId,
        [
            new TestSuite("Tests",
            [
                new TestResult("Tests.Captures", "Captures", TestStatus.Passed, TimeSpan.Zero, null, null, null, null)
                {
                    Attachments = attachments,
                },
            ], DateTimeOffset.UnixEpoch, TimeSpan.Zero),
        ]);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"piston-artifacts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
