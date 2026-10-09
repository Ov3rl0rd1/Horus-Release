using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using Horus.Platforms.Windows.Tunnel;

namespace Horus.Platforms.Windows
{
    /// <summary>An application the user has chosen, remembered whether or not it is running.</summary>
    public sealed record SavedApp(string Exe, string? Path, string? Name);

    /// <summary>
    /// Per-application split tunneling on Windows, done by the core's routing.
    ///
    /// <para><b>Why it never worked before.</b> The previous version watched new connections
    /// with WinDivert and added a host route for each destination of a chosen app. It could
    /// not have worked: its P/Invoke named <c>WinDivertHelperNtohIpv4Address</c>, which
    /// WinDivert.dll does not export, so every event threw
    /// <c>EntryPointNotFoundException</c> into a silent catch and no route was ever added.
    /// And had it worked, a route added after the connection opened leaves that connection
    /// broken, and a host route applies to every application talking to that address.</para>
    ///
    /// <para><b>How it works now.</b> The core owns the TUN, so each connection carries the
    /// source of the application's socket and the core's <c>process</c> rule matches the
    /// executable. The choice is a routing rule, applied by reloading the core's rules — no
    /// restart, no adapter churn, nothing installed in Windows. Open connections keep the
    /// route they started with; "restart connections" on the connections screen moves them.
    /// No WinDivert driver either, which anti-cheat software is known to object to.</para>
    ///
    /// <para><b>Persistence.</b> The mode and the chosen apps survive restarts, and an app the
    /// user chose stays in the list when it is not running — the previous list showed only
    /// what happened to be running, so a choice vanished with the window.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsSplitTunnelingService : ISplitTunnelingService
    {
        private const string ModeKey = "win.split.mode";
        private const string AppsKey = "win.split.apps";

        private readonly object _gate = new();
        private SplitTunnelingMode _mode;
        private List<SavedApp> _saved;

        public WindowsSplitTunnelingService()
        {
            _mode = (SplitTunnelingMode)Read(ModeKey, (int)SplitTunnelingMode.Disabled);
            _saved = LoadSaved();
        }

        /// <summary>Always: the rules are the core's, and they work with any build of it that has a TUN.</summary>
        public bool IsSupported => true;

        /// <summary>The list is processes, so a running window is a meaningful distinction.</summary>
        public bool DistinguishesWindows => true;

        public SplitTunnelingMode Mode
        {
            get => _mode;
            set
            {
                if (_mode == value) return;
                _mode = value;
                Write(ModeKey, (int)value);
                Diag.User("split", $"mode {value}");
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                RulesChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public IReadOnlyList<string> SelectedEntries
        {
            get { lock (_gate) return _saved.Select(a => a.Exe).ToList(); }
        }

        public IReadOnlyList<SavedApp> SavedApps
        {
            get { lock (_gate) return [.. _saved]; }
        }

        /// <summary>Package names forced direct are an Android notion; nothing here.</summary>
        public IReadOnlyList<string> AlwaysDirectEntries => [];

        public event EventHandler? SelectionChanged;

        /// <summary>The routing rules this service implies have changed and should be applied.</summary>
        public event EventHandler? RulesChanged;

        /// <summary>What the core should do, as of now.</summary>
        public WindowsSplitRules CurrentRules
        {
            get
            {
                lock (_gate)
                    return new WindowsSplitRules(_mode, [.. _saved.Select(a => a.Exe)]);
            }
        }

        public Task<IReadOnlyList<AppOrProcessEntry>> GetAvailableEntriesAsync() =>
            Task.Run<IReadOnlyList<AppOrProcessEntry>>(() =>
            {
                // Keyed by executable name, because that is what a rule matches: several
                // chrome.exe are one entry, named by the instance that has a window.
                var byExe = new Dictionary<string, AppOrProcessEntry>(StringComparer.OrdinalIgnoreCase);

                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        var path = proc.MainModule?.FileName;
                        if (string.IsNullOrEmpty(path)) continue;
                        var exe = System.IO.Path.GetFileName(path);
                        if (string.IsNullOrEmpty(exe) || IsOwnProcess(path)) continue;

                        var title = proc.MainWindowTitle;
                        var windowed = title is { Length: > 0 };

                        if (!byExe.TryGetValue(exe, out var entry))
                        {
                            byExe[exe] = new AppOrProcessEntry
                            {
                                Id = exe,
                                DisplayName = FriendlyName(path) ?? (windowed ? title! : System.IO.Path.GetFileNameWithoutExtension(exe)),
                                Path = path,
                                HasWindow = windowed,
                                IconPath = CachedIcon(path),
                                IsSystem = IsSystemPath(path)
                            };
                        }
                        else if (windowed && !entry.HasWindow)
                        {
                            entry.HasWindow = true;
                        }
                    }
                    catch { /* access denied is ordinary for protected processes */ }
                    finally { proc.Dispose(); }
                }

                // Chosen apps stay listed when they are not running — otherwise a choice
                // disappears with the window, and nobody can tell whether it still applies.
                foreach (var app in SavedApps)
                {
                    if (byExe.ContainsKey(app.Exe)) continue;
                    byExe[app.Exe] = new AppOrProcessEntry
                    {
                        Id = app.Exe,
                        DisplayName = app.Name ?? System.IO.Path.GetFileNameWithoutExtension(app.Exe),
                        Path = app.Path is null ? "не запущено" : $"{app.Path} · не запущено",
                        HasWindow = false,
                        IconPath = app.Path is null ? null : CachedIcon(app.Path),
                        IsSystem = false
                    };
                }

                var chosen = SelectedEntries.ToHashSet(StringComparer.OrdinalIgnoreCase);
                return byExe.Values
                    .OrderByDescending(e => chosen.Contains(e.Id))
                    .ThenByDescending(e => e.HasWindow)
                    .ThenBy(e => e.IsSystem)
                    .ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            });

        public Task SetSelectedEntriesAsync(IEnumerable<string> entries)
        {
            var wanted = entries.Where(e => !string.IsNullOrWhiteSpace(e))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            lock (_gate)
            {
                var known = _saved.ToDictionary(a => a.Exe, StringComparer.OrdinalIgnoreCase);
                _saved = wanted.Select(exe => known.TryGetValue(exe, out var a) ? a : Describe(exe)).ToList();
            }

            Persist();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Chooses or un-chooses one application and applies the rule at once. Unlike
        /// <see cref="SetSelectedEntriesAsync"/> it needs no process scan: the entry already
        /// carries the path and name the saved record keeps.
        /// </summary>
        public void SetChosen(string exe, string? path, string? name, bool chosen)
        {
            lock (_gate)
            {
                var existing = _saved.FindIndex(a => a.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase));
                if (chosen && existing < 0) _saved.Add(new SavedApp(exe, path, name));
                else if (!chosen && existing >= 0) _saved.RemoveAt(existing);
                else return;
            }
            Persist();
            Diag.User("split", $"{(chosen ? "chose" : "dropped")} {exe}");
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            RulesChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Chooses an application by its executable — for one that is not running, or has never run.</summary>
        public SavedApp Add(string exePath)
        {
            var app = new SavedApp(System.IO.Path.GetFileName(exePath), exePath, FriendlyName(exePath));
            lock (_gate)
            {
                _saved.RemoveAll(a => a.Exe.Equals(app.Exe, StringComparison.OrdinalIgnoreCase));
                _saved.Add(app);
            }
            Persist();
            Diag.User("split", $"added {app.Exe}");
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            RulesChanged?.Invoke(this, EventArgs.Empty);
            return app;
        }

        /// <summary>The icon file for an executable, extracting it on first use; null if Windows has none.</summary>
        public async Task<string?> IconForAsync(string exePath, CancellationToken ct = default)
        {
            if (CachedIcon(exePath) is { } cached) return cached;
            if (!File.Exists(exePath) || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return null;
            try { return await ExeIcons.ExtractAsync(exePath, IconFile(exePath), ct); }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex) { Debug.WriteLine($"[Horus] icon {exePath}: {ex.Message}"); return null; }
        }

        /// <summary>Asks for the current rules to be applied; the controller reloads the core's routing.</summary>
        public Task ApplyAsync()
        {
            RulesChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Icons from Windows' own thumbnail of the executable, cached as PNG. The list is
        /// on screen before they arrive.
        /// </summary>
        public async Task LoadIconsAsync(
            IReadOnlyList<AppOrProcessEntry> entries, Action<AppOrProcessEntry> onReady, CancellationToken ct = default)
        {
            foreach (var entry in entries)
            {
                if (ct.IsCancellationRequested) return;
                if (entry.IconPath is not null) continue;

                var path = entry.Path?.Split(" · ")[0];
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;

                // The thumbnail API is Windows 10 1809+; older systems simply get no icons.
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
                try
                {
                    var icon = await ExeIcons.ExtractAsync(path, IconFile(path), ct);
                    if (icon is null) continue;
                    entry.IconPath = icon;
                    onReady(entry);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Debug.WriteLine($"[Horus] icon {path}: {ex.Message}"); }
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static SavedApp Describe(string exe)
        {
            // Called for a newly chosen entry: find the running process for its path, so the
            // app can still be shown with an icon and a name once it has exited.
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (path is not null && System.IO.Path.GetFileName(path).Equals(exe, StringComparison.OrdinalIgnoreCase))
                        return new SavedApp(System.IO.Path.GetFileName(path), path, FriendlyName(path));
                }
                catch { }
                finally { proc.Dispose(); }
            }
            return new SavedApp(exe, null, null);
        }

        /// <summary>The product's own name from the file's version resource ("Discord", not "Update").</summary>
        private static string? FriendlyName(string path)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription : info.ProductName;
                return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            }
            catch { return null; }
        }

        private static bool IsOwnProcess(string path) =>
            string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);

        private static bool IsSystemPath(string path) =>
            path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase);

        private static string IconFile(string exePath)
        {
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(exePath.ToLowerInvariant())))[..16];
            return System.IO.Path.Combine(FileSystem.CacheDirectory, "app-icons", hash + ".png");
        }

        private static string? CachedIcon(string exePath)
        {
            var file = IconFile(exePath);
            return File.Exists(file) ? file : null;
        }

        private void Persist()
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_saved);
            Write(AppsKey, json);
        }

        private static List<SavedApp> LoadSaved()
        {
            try
            {
                var json = Read(AppsKey, string.Empty);
                return json.Length == 0 ? [] : JsonSerializer.Deserialize<List<SavedApp>>(json) ?? [];
            }
            catch { return []; }
        }

        private static T Read<T>(string key, T fallback)
        {
            try { return Preferences.Default.Get(key, fallback); } catch { return fallback; }
        }

        private static void Write<T>(string key, T value)
        {
            try { Preferences.Default.Set(key, value); } catch (Exception ex) { Diag.Warn("split", $"not saved: {ex.Message}"); }
        }
    }
}
