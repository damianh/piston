// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;

namespace Piston.Engine.Execution;

/// <summary>Per-user trust decisions for repository clones.</summary>
public sealed class RepositoryTrustStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);
    private readonly Lock _gate = new();

    public RepositoryTrustStore(string filePath)
    {
        if (!Path.IsPathFullyQualified(filePath))
        {
            throw new ArgumentException("The trust store path must be absolute.", nameof(filePath));
        }

        FilePath = Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public static string ResolveDefaultPath()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(localAppData))
        {
            throw new ExecutionCoordinationException(
                "Cannot determine the per-user local application data folder for repository trust.");
        }

        return Path.Combine(localAppData, "Piston", "trust", "repositories.json");
    }

    public bool IsTrusted(CloneIdentity clone) => Read().Repositories.Any(repository => repository.Key == clone.Key);

    public IReadOnlyList<TrustedRepository> List() =>
        Read().Repositories.OrderBy(repository => repository.Path, StringComparer.Ordinal).ToArray();

    public void Trust(CloneIdentity clone) => Update(document =>
    {
        document.Repositories.RemoveAll(repository => repository.Key == clone.Key);
        document.Repositories.Add(new TrustedRepository(clone.Key, clone.RootPath));
    });

    public bool Revoke(CloneIdentity clone) => Update(document =>
        document.Repositories.RemoveAll(repository => repository.Key == clone.Key) > 0);

    private TrustDocument Read()
    {
        lock (_gate)
        using (AcquireFileLock())
        {
            return ReadDocument();
        }
    }

    private T Update<T>(Func<TrustDocument, T> action)
    {
        lock (_gate)
        using (AcquireFileLock())
        {
            var document = ReadDocument();
            var result = action(document);
            WriteDocument(document);
            return result;
        }
    }

    private TrustDocument ReadDocument()
    {
        try
        {
            var document = JsonSerializer.Deserialize<TrustDocument>(File.ReadAllText(FilePath), JsonOptions);
            if (document is not { Version: 1, Repositories: not null }
                || document.Repositories.Any(repository => string.IsNullOrWhiteSpace(repository.Key)
                    || string.IsNullOrWhiteSpace(repository.Path)))
            {
                throw new JsonException("The repository trust document has an unsupported shape.");
            }

            return document;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new TrustDocument();
        }
        catch (JsonException ex)
        {
            throw new ExecutionCoordinationException($"Cannot read repository trust file '{FilePath}': {ex.Message}", ex);
        }
    }

    private void WriteDocument(TrustDocument document)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        _ = Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var temporaryPath = Path.Combine(directory, $".repositories.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = File.Create(temporaryPath))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private FileStream AcquireFileLock()
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        _ = Directory.CreateDirectory(directory);
        var lockPath = FilePath + ".lock";
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
            catch (IOException ex)
            {
                throw new ExecutionCoordinationException(
                    $"Cannot acquire repository trust lock '{lockPath}': {ex.Message}", ex);
            }
        }
    }

    private sealed class TrustDocument
    {
        public int Version { get; set; } = 1;
        public List<TrustedRepository> Repositories { get; set; } = [];
    }
}

public sealed record TrustedRepository(string Key, string Path);
