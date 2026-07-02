using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application
{
    public class StubProcessRunner : IProcessRunner
    {
        public Task<ProcessHandle> StartAsync(string executable, string[] args, string? workDir = null) =>
            Task.FromException<ProcessHandle>(
                new PlatformNotSupportedException("Process runner not supported on this platform."));

        public Task StopAsync(ProcessHandle handle) => Task.CompletedTask;
    }
}
