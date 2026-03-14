using Piston.Engine.Models;

namespace Piston.Engine.Services;

public interface IFileWatcherService : IDisposable
{
    event Action<FileChangeBatch> FileChanged;
    void Start(string solutionDirectory);
    void Stop();
}
