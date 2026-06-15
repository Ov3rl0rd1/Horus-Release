namespace Horus.Domain.Models
{
    public class ProcessHandle
    {
        public int Pid { get; init; }
        internal System.Diagnostics.Process? ProcessRef { get; init; }
    }
}
