using Piston.Engine.Models;

namespace Piston.Engine.Services;

public interface IFileWatcherService : IDisposable
{
    event Action<FileChangeEvent> FileChanged;
    void Start(string solutionDirectory);
    void Stop();
}
