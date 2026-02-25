using Piston.Core.Models;

namespace Piston.Core.Services;

public interface IFileWatcherService : IDisposable
{
    event Action<FileChangeEvent> FileChanged;
    void Start(string solutionDirectory);
    void Stop();
}
