using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    public interface IProcessRunner
    {
        Task<ProcessHandle> StartAsync(string executable, string[] args, string? workDir = null);
        Task StopAsync(ProcessHandle handle);
    }
}
