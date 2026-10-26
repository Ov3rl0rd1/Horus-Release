using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Horus.Domain.Models;

namespace Horus.Platforms.Windows.Tunnel
{
    /// <summary>
    /// Keeps <see cref="Followers"/> — the helpers the chosen applications started — current
    /// while the tunnel is up, and says when it changes so the rules can be reloaded.
    ///
    /// <para>Looks every <see cref="Interval"/>: a process snapshot lists ids, parents and names
    /// for every process in about a millisecond, and only the members of a chosen family are
    /// opened, once each.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class ProcessFamilyWatcher
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

        private readonly object _gate = new();
        private readonly Dictionary<(int Id, string Name), ProcessDetail?> _details = [];
        private readonly Dictionary<int, string> _listedNames = [];
        private IReadOnlyList<string> _followers = [];
        private IReadOnlyList<string> _watchedFor = [];
        private CancellationTokenSource? _cts;

        public IReadOnlyList<string> Followers => Volatile.Read(ref _followers);

        /// <summary>Raised off the UI thread when <see cref="Followers"/> changed.</summary>
        public event EventHandler? Changed;

        /// <param name="rules">Read at every look, so a change of the user's choice needs no restart.</param>
        public void Start(Func<WindowsSplitRules> rules)
        {
            Stop();
            var cts = new CancellationTokenSource();
            _cts = cts;
            _ = Task.Run(() => WatchAsync(rules, cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            _cts = null;
            Volatile.Write(ref _followers, []);
        }

        private async Task WatchAsync(Func<WindowsSplitRules> rules, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    lock (_gate) Look(rules());
                    await Task.Delay(Interval, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Diag.Error("split", $"helper watch died: {ex.Message}"); }
        }

        private void Look(WindowsSplitRules rules)
        {
            var chosen = rules.IsActive ? rules.Processes : [];
            var known = chosen.SequenceEqual(_watchedFor, StringComparer.OrdinalIgnoreCase) ? Followers : [];
            _watchedFor = chosen;

            var listed = chosen.Count == 0 ? [] : Snapshot();
            var found = ProcessFamily.DescendantImages(listed, chosen, Describe, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            var followers = ProcessFamily.Remember(known, found, ProcessFamily.AnyRunning(listed, chosen));
            if (followers.SequenceEqual(Followers, StringComparer.OrdinalIgnoreCase)) return;

            Volatile.Write(ref _followers, followers);
            Diag.Info("split", $"helpers of the chosen apps: {(followers.Count == 0 ? "none" : string.Join(", ", followers.Select(Path.GetFileName)))}");
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Every process now, forgetting the details of those that have exited.</summary>
        private List<ListedProcess> Snapshot()
        {
            var listed = Native.Snapshot();
            _listedNames.Clear();
            foreach (var process in listed) _listedNames[process.Id] = process.Name;

            foreach (var gone in _details.Keys.Where(k => !_listedNames.TryGetValue(k.Id, out var name) || name != k.Name).ToList())
                _details.Remove(gone);
            return listed;
        }

        /// <summary>Opened once per process: keyed by id and name, so an id another program reuses is a new entry.</summary>
        private ProcessDetail? Describe(int id)
        {
            var key = (id, _listedNames.GetValueOrDefault(id, string.Empty));
            if (!_details.TryGetValue(key, out var detail)) _details[key] = detail = Native.Describe(id);
            return detail;
        }

        private static class Native
        {
            private const uint SnapProcess = 0x2;
            private const uint QueryLimitedInformation = 0x1000;
            private static readonly IntPtr InvalidHandle = new(-1);

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct ProcessEntry32
            {
                public uint Size;
                public uint Usage;
                public uint ProcessId;
                public IntPtr DefaultHeapId;
                public uint ModuleId;
                public uint Threads;
                public uint ParentProcessId;
                public int PriorityClassBase;
                public uint Flags;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, char[] name, ref int size);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

            [DllImport("kernel32.dll")]
            private static extern bool CloseHandle(IntPtr handle);

            public static List<ListedProcess> Snapshot()
            {
                var result = new List<ListedProcess>();
                var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
                if (snapshot == InvalidHandle) return result;
                try
                {
                    var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
                    for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                        result.Add(new ListedProcess((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile));
                }
                finally { CloseHandle(snapshot); }
                return result;
            }

            public static ProcessDetail? Describe(int id)
            {
                var process = OpenProcess(QueryLimitedInformation, false, id);
                if (process == IntPtr.Zero) return null;
                try
                {
                    var name = new char[32_768];
                    var size = name.Length;
                    if (!QueryFullProcessImageNameW(process, 0, name, ref size)) return null;
                    if (!GetProcessTimes(process, out var created, out _, out _, out _)) return null;
                    return new ProcessDetail(new string(name, 0, size), created);
                }
                finally { CloseHandle(process); }
            }
        }
    }
}
