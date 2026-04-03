using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Platforms.Android
{
    public class AndroidProcessRunner : IProcessRunner
    {
        public Task<ProcessHandle> StartAsync(string executable, string[] args, string? workDir = null)
        {
            throw new NotImplementedException();
        }

        public Task StopAsync(ProcessHandle handle)
        {
            throw new NotImplementedException();
        }
    }
}
