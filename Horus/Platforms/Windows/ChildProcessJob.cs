using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Horus.Platforms.Windows
{
    /// <summary>
    /// A Windows job object that kills everything assigned to it the moment its last handle
    /// closes — which the kernel does when this process ends, however it ends.
    ///
    /// This is a safety device, not a convenience. The TUN bridge runs as a child process,
    /// and a child does not die with its parent on Windows. If Horus is killed, crashes, or
    /// is closed while connected, an orphaned bridge keeps its adapter alive, the default
    /// routes keep pointing into it, and the core that was supposed to be on the other end
    /// is gone — the machine has no working route to anywhere and no obvious way back. The
    /// user's report was exactly that: no internet until a reboot.
    ///
    /// With the bridge in a job, the adapter disappears within a second of the app dying and
    /// the routes go with it, because Windows drops an interface's routes when the interface
    /// goes away. Recovery needs no user action and no reboot.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class ChildProcessJob : IDisposable
    {
        private const int ExtendedLimitInformationClass = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        private nint _handle;

        public ChildProcessJob()
        {
            _handle = CreateJobObjectW(nint.Zero, null);
            if (_handle == nint.Zero)
                throw new InvalidOperationException(
                    $"CreateJobObject failed ({Marshal.GetLastWin32Error()}).");

            var info = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = { LimitFlags = JobObjectLimitKillOnJobClose }
            };

            var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);

                if (!SetInformationJobObject(_handle, ExtendedLimitInformationClass, buffer, (uint)size))
                    throw new InvalidOperationException(
                        $"SetInformationJobObject failed ({Marshal.GetLastWin32Error()}).");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        /// <summary>
        /// Brings <paramref name="process"/> under the job. A failure here is fatal to the
        /// caller's intent: an unassigned child is precisely the orphan this class exists to
        /// prevent, so the caller must not carry on as if it had worked.
        /// </summary>
        public void Assign(Process process)
        {
            if (!AssignProcessToJobObject(_handle, process.Handle))
                throw new InvalidOperationException(
                    $"AssignProcessToJobObject failed ({Marshal.GetLastWin32Error()}).");
        }

        public void Dispose()
        {
            if (_handle == nint.Zero) return;

            CloseHandle(_handle);
            _handle = nint.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateJobObjectW(nint attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            nint job, int infoClass, nint info, uint infoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(nint job, nint process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint handle);
    }
}
