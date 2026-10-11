using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using Horus.Platforms.Windows.Tunnel;
using Horus.Presentation.Navigation;

namespace Horus.Platforms.Windows.Views
{
    /// <summary>One line of the application list: the user's choice and what the app is doing now.</summary>
    public sealed class AppRow : ObservableObject
    {
        public AppRow(string exe) => Exe = exe;

        /// <summary>Executable name — what a rule matches, and the row's identity.</summary>
        public string Exe { get; }

        private string _name = string.Empty;
        public string Name { get => _name; set => SetProperty(ref _name, value); }

        private string? _path;
        /// <summary>Full image path when known; null for a remembered app whose file was never seen.</summary>
        public string? ExePath { get => _path; set => SetProperty(ref _path, value); }

        private ImageSource? _icon;
        public ImageSource? Icon
        {
            get => _icon;
            set { if (SetProperty(ref _icon, value)) OnPropertyChanged(nameof(HasIcon)); }
        }
        public bool HasIcon => _icon is not null;
        public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";

        private bool _running;
        public bool IsRunning { get => _running; set => SetProperty(ref _running, value); }

        public bool IsSystem { get; set; }
        public bool HasWindow { get; set; }

        private bool _chosen;
        public bool IsChosen { get => _chosen; set => SetProperty(ref _chosen, value); }

        private AppTraffic? _live;
        /// <summary>The app's open connections in the core; null when it has none.</summary>
        public AppTraffic? Live
        {
            get => _live;
            set
            {
                if (!SetProperty(ref _live, value)) return;
                OnPropertyChanged(nameof(HasConnections));
            }
        }
        public bool HasConnections => _live is { Total: > 0 };

        private string _subtitle = string.Empty;
        public string Subtitle { get => _subtitle; set => SetProperty(ref _subtitle, value); }

        private string _routeText = string.Empty;
        public string RouteText { get => _routeText; set => SetProperty(ref _routeText, value); }

        private RouteMix _route = RouteMix.Other;
        public RouteMix Route { get => _route; set => SetProperty(ref _route, value); }

        private bool _needsRestart;
        /// <summary>Its open connections predate the latest rule change, so they still go the old way.</summary>
        public bool NeedsRestart { get => _needsRestart; set => SetProperty(ref _needsRestart, value); }

        private string _choiceCaption = string.Empty;
        /// <summary>What the switch does in the current mode ("Мимо VPN", "Через VPN").</summary>
        public string ChoiceCaption { get => _choiceCaption; set => SetProperty(ref _choiceCaption, value); }

        private bool _canChoose;
        /// <summary>False with split tunnelling off: there is nothing to choose.</summary>
        public bool CanChoose { get => _canChoose; set => SetProperty(ref _canChoose, value); }

        /// <summary>When the rule for this app last changed (Unix ms); 0 for never in this session.</summary>
        public long RuleChangedAtMs { get; set; }

        /// <summary>Sort weight: chosen, then with traffic, then with a window, then the rest.</summary>
        public int Rank => IsChosen ? 0 : HasConnections ? 1 : HasWindow ? 2 : IsSystem ? 4 : 3;
    }

    /// <summary>
    /// The Windows "Приложения" screen: split-tunnel mode, the per-application choice, and the
    /// live route of every application with a way to make it reconnect.
    ///
    /// <para>Replaces the shared split-tunnelling picker on Windows, which listed only running
    /// processes (a chosen app vanished when closed), had no icons, and did not show whether a
    /// choice had taken effect. Here a chosen app is always listed; running or not is a
    /// subtitle. The route shown is the core's own record of each connection, not a guess.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class AppsViewModel : ObservableObject
    {
        private static readonly TimeSpan LiveInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ProcessInterval = TimeSpan.FromSeconds(12);

        private readonly WindowsSplitTunnelingService _split;
        private readonly WindowsConnectionMonitor _connections;
        private readonly IVpnController _vpn;
        private readonly Dictionary<string, AppRow> _rows = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _iconsRequested = new(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource? _loop;
        private DateTime _processesAt = DateTime.MinValue;
        private long _modeChangedAtMs;

        public AppsViewModel(ISplitTunnelingService split, WindowsConnectionMonitor connections, IVpnController vpn)
        {
            _split = (WindowsSplitTunnelingService)split;
            _connections = connections;
            _vpn = vpn;
            _vpn.StateChanged += (_, _) => MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(IsConnected)));
        }

        /// <summary>The rows on screen, filtered and sorted.</summary>
        public ObservableCollection<AppRow> Visible { get; } = [];

        public SplitTunnelingMode Mode => _split.Mode;
        public bool IsConnected => _vpn.State == VpnState.Connected;

        private string _search = string.Empty;
        public string Search
        {
            get => _search;
            set { if (SetProperty(ref _search, value ?? string.Empty)) Rebuild(); }
        }

        private bool _showBackground;
        /// <summary>Include processes with no window and no traffic — services, helpers, updaters.</summary>
        public bool ShowBackground
        {
            get => _showBackground;
            set { if (SetProperty(ref _showBackground, value)) Rebuild(); }
        }

        private string _summary = string.Empty;
        public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

        private int _staleCount;
        /// <summary>Apps whose open connections still follow an older rule.</summary>
        public int StaleCount
        {
            get => _staleCount;
            private set { if (SetProperty(ref _staleCount, value)) OnPropertyChanged(nameof(HasStale)); }
        }
        public bool HasStale => _staleCount > 0;

        public bool LiveSupported => _connections.IsSupported;

        /// <summary>Raised after the list or the live figures change; the view re-reads what it shows.</summary>
        public event EventHandler? Refreshed;

        // ── Lifecycle ────────────────────────────────────────────────────────

        public void SetVisible(bool visible)
        {
            _loop?.Cancel();
            _loop = null;
            if (!visible) return;

            var cts = new CancellationTokenSource();
            _loop = cts;
            _ = RunAsync(cts.Token);
        }

        private async Task RunAsync(CancellationToken ct)
        {
            _processesAt = DateTime.MinValue;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (DateTime.UtcNow - _processesAt > ProcessInterval) await LoadProcessesAsync();
                    await RefreshLiveAsync();
                    await Task.Delay(LiveInterval, ct);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Diag.Warn("apps", $"refresh failed: {ex.Message}"); await Task.Delay(LiveInterval, CancellationToken.None); }
            }
        }

        /// <summary>The user asked for a fresh look (the refresh button).</summary>
        public async Task RefreshNowAsync()
        {
            await LoadProcessesAsync();
            await RefreshLiveAsync();
        }

        // ── Data ─────────────────────────────────────────────────────────────

        private async Task LoadProcessesAsync()
        {
            _processesAt = DateTime.UtcNow;
            var entries = await _split.GetAvailableEntriesAsync();
            var chosen = _split.SelectedEntries.ToHashSet(StringComparer.OrdinalIgnoreCase);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in entries)
                {
                    seen.Add(e.Id);
                    var row = RowFor(e.Id);
                    var path = e.Path?.Split(" · ")[0];
                    row.Name = e.DisplayName;
                    row.ExePath = path is { Length: > 0 } && path != "не запущено" ? path : row.ExePath;
                    row.IsRunning = e.Path?.EndsWith("не запущено", StringComparison.Ordinal) != true;
                    row.IsSystem = e.IsSystem;
                    row.HasWindow = e.HasWindow;
                    row.IsChosen = chosen.Contains(e.Id);
                    if (row.Icon is null && e.IconPath is not null) row.Icon = ImageSource.FromFile(e.IconPath);
                }
                foreach (var stale in _rows.Keys.Where(k => !seen.Contains(k) && _rows[k].Live is null).ToList())
                    _rows.Remove(stale);

                Rebuild();
            });
        }

        private async Task RefreshLiveAsync()
        {
            var live = IsConnected && _connections.IsSupported
                ? await Task.Run(() => _connections.Snapshot())
                : (IReadOnlyList<AppTraffic>)[];

            MainThread.BeginInvokeOnMainThread(() =>
            {
                var byName = live.Where(a => a.Name != AppTrafficGrouping.Unknown)
                    .ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

                foreach (var a in byName.Values)
                {
                    if (_rows.ContainsKey(a.Name)) continue;
                    // Traffic from something the process scan has not listed yet (it runs less often).
                    var row = RowFor(a.Name);
                    row.Name = System.IO.Path.GetFileNameWithoutExtension(a.Name);
                    row.ExePath = a.Path;
                    row.IsRunning = true;
                }

                var stale = 0;
                foreach (var row in _rows.Values)
                {
                    row.Live = byName.GetValueOrDefault(row.Exe);
                    row.NeedsRestart = AppRoutePolicy.NeedsRestart(row.Live, Math.Max(row.RuleChangedAtMs, _modeChangedAtMs));
                    if (row.NeedsRestart) stale++;
                    Describe(row);
                }
                StaleCount = stale;

                var vpn = live.Count(a => a.ViaVpn > 0);
                var direct = live.Count(a => a.Direct > 0);
                Summary = !IsConnected ? "VPN не подключён — маршруты появятся после подключения"
                    : !_connections.IsSupported ? "Ядро этой сборки не сообщает соединения"
                    : live.Count == 0 ? "Соединений пока нет"
                    : $"Через VPN: {Apps(vpn)} · напрямую: {Apps(direct)}";

                Rebuild();
            });
        }

        private static string Apps(int n) => n % 10 == 1 && n % 100 != 11 ? $"{n} приложение"
            : n % 10 is >= 2 and <= 4 && (n % 100 < 10 || n % 100 >= 20) ? $"{n} приложения"
            : $"{n} приложений";

        private void Describe(AppRow row)
        {
            if (row.Live is { } live)
            {
                row.Route = live.Mix;
                row.RouteText = AppRoutePolicy.Describe(live.Mix);
                var count = live.Total;
                var noun = count % 10 == 1 && count % 100 != 11 ? "соединение"
                    : count % 10 is >= 2 and <= 4 && (count % 100 < 10 || count % 100 >= 20) ? "соединения" : "соединений";
                row.Subtitle = $"{count} {noun} · ↓ {Ui.Bytes(live.Down)} ↑ {Ui.Bytes(live.Up)}";
            }
            else
            {
                row.Route = RouteMix.Other;
                row.RouteText = string.Empty;
                row.Subtitle = row.IsRunning ? (row.HasWindow ? "Запущено" : "Фоновый процесс") : "Не запущено";
            }
        }

        private AppRow RowFor(string exe)
        {
            if (!_rows.TryGetValue(exe, out var row))
            {
                _rows[exe] = row = new AppRow(exe);
                ApplyMode(row);
            }
            return row;
        }

        private void ApplyMode(AppRow row)
        {
            row.CanChoose = _split.Mode != SplitTunnelingMode.Disabled;
            row.ChoiceCaption = AppRoutePolicy.ToggleCaption(_split.Mode);
        }

        /// <summary>Filters and sorts into <see cref="Visible"/>, touching the collection only where it changed.</summary>
        private void Rebuild()
        {
            var query = _search.Trim();
            var wanted = _rows.Values
                .Where(r => query.Length == 0
                    ? r.IsChosen || r.HasConnections || r.HasWindow || _showBackground
                    : r.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || r.Exe.Contains(query, StringComparison.OrdinalIgnoreCase))
                // By group, then by name — not by traffic, which would reshuffle the rows
                // under the pointer every two seconds.
                .OrderBy(r => r.Rank)
                .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (wanted.SequenceEqual(Visible))
            {
                Refreshed?.Invoke(this, EventArgs.Empty);
                return;
            }

            // Reconcile rather than Clear + Add: a full reset every two seconds would reset the
            // scroll position and flicker every row.
            for (var i = Visible.Count - 1; i >= 0; i--)
                if (!wanted.Contains(Visible[i])) Visible.RemoveAt(i);
            for (var i = 0; i < wanted.Count; i++)
            {
                var at = Visible.IndexOf(wanted[i]);
                if (at == i) continue;
                if (at >= 0) Visible.Move(at, i);
                else Visible.Insert(i, wanted[i]);
            }

            RequestIcons(wanted);
            Refreshed?.Invoke(this, EventArgs.Empty);
        }

        private void RequestIcons(IEnumerable<AppRow> rows)
        {
            foreach (var row in rows)
            {
                if (row.Icon is not null || row.ExePath is null || !_iconsRequested.Add(row.Exe)) continue;
                var path = row.ExePath;
                _ = Task.Run(async () =>
                {
                    var icon = await _split.IconForAsync(path);
                    if (icon is not null) MainThread.BeginInvokeOnMainThread(() => row.Icon = ImageSource.FromFile(icon));
                });
            }
        }

        // ── Actions ──────────────────────────────────────────────────────────

        public void SetMode(SplitTunnelingMode mode)
        {
            if (_split.Mode == mode) return;
            _split.Mode = mode;   // raises RulesChanged; the controller reloads routing live
            _modeChangedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var row in _rows.Values) ApplyMode(row);
            OnPropertyChanged(nameof(Mode));
            _ = RefreshLiveAsync();
        }

        public void SetChosen(AppRow row, bool chosen)
        {
            if (row.IsChosen == chosen && _split.SelectedEntries.Contains(row.Exe, StringComparer.OrdinalIgnoreCase) == chosen) return;
            row.IsChosen = chosen;
            row.RuleChangedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _split.SetChosen(row.Exe, row.ExePath, row.Name, chosen);
            _ = RefreshLiveAsync();
        }

        /// <summary>Closes the app's connections in the core; it reconnects under the current rules.</summary>
        public int Restart(AppRow row)
        {
            if (row.Live is not { } live) return 0;
            var closed = _connections.Restart(live);
            row.RuleChangedAtMs = 0;
            _ = RefreshLiveAsync();
            return closed;
        }

        /// <summary>Restarts every app whose connections still follow an older rule.</summary>
        public int RestartStale()
        {
            var closed = 0;
            foreach (var row in _rows.Values.Where(r => r.NeedsRestart).ToList())
                closed += Restart(row);
            _modeChangedAtMs = 0;
            return closed;
        }

        public async Task AddExecutableAsync()
        {
            string? path;
            try { path = await ExePicker.PickAsync("Выберите программу"); }
            catch (Exception ex)
            {
                Diag.Warn("apps", $"picker failed: {ex.Message}");
                await Dialog.Alert("Не удалось открыть выбор файла", ex.Message);
                return;
            }
            if (string.IsNullOrEmpty(path)) return;

            var saved = _split.Add(path);
            var row = RowFor(saved.Exe);
            row.Name = saved.Name ?? System.IO.Path.GetFileNameWithoutExtension(saved.Exe);
            row.ExePath = path;
            row.IsChosen = true;
            row.RuleChangedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _iconsRequested.Remove(saved.Exe);
            Describe(row);
            Rebuild();
        }
    }
}
