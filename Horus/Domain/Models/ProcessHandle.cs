namespace Horus.Domain.Models
{
    public class ProcessHandle
    {
        public int Pid { get; init; }
        internal System.Diagnostics.Process? ProcessRef { get; init; }

        public ProcessHandle() { }

        public ProcessHandle(int pid, System.Diagnostics.Process? processRef)
        {
            Pid = pid;
            ProcessRef = processRef;
        }
    }
}
