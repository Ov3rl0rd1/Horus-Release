using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Presentation.ViewModels
{
    /// <summary>
    /// Server picker. Server list and per-server load come from the real
    /// <see cref="ISubscriptionService"/>. Per-server <b>ping</b> is a placeholder —
    /// there is no latency-measurement service yet, so rows show server load only
    /// (see PLAN-remaining-functions.md).
    /// </summary>
    public partial class ServersViewModel : ObservableObject
    {
        private readonly ISubscriptionService _subscription;
        private readonly AppSession _session;

        private List<ServerInfo> _all = new();

        [ObservableProperty] private string _searchQuery = string.Empty;
        [ObservableProperty] private bool _isLoading;
        [ObservableProperty] private bool _isAutoSelected = true;
        [ObservableProperty] private bool _hasError;

        /// <summary>True once the server list has been fetched — avoids re-loading on every tab switch.</summary>
        public bool HasLoaded { get; private set; }

        public ObservableCollection<ServerRow> Recommended { get; } = new();
        public ObservableCollection<ServerRow> AllServers { get; } = new();

        public bool HasRecommended => Recommended.Count > 0;
        public bool HasAll => AllServers.Count > 0;
        public bool NoResults => !IsLoading && Recommended.Count == 0 && AllServers.Count == 0 && _all.Count > 0;
        public int LocationCount => _all.Count;
        public string LocationCountText => $"{_all.Count} локаций";

        public ServersViewModel(ISubscriptionService subscription, AppSession session)
        {
            _subscription = subscription;
            _session = session;
            _session.PropertyChanged += (_, __) => SyncSelection();
        }

        partial void OnSearchQueryChanged(string value) => Rebuild();

        [RelayCommand]
        public async Task LoadAsync()
        {
            if (IsLoading) return;
            IsLoading = true;
            HasError = false;
            try
            {
                // Measured here and nowhere else. The probe costs a TCP connect per
                // candidate, and this is the one screen where the answer is worth that —
                // the Home screen only needs names.
                var servers = await _subscription.GetAvailableServersAsync(measureLatency: true);
                _all = servers?.ToList() ?? new List<ServerInfo>();
            }
            catch
            {
                _all = new List<ServerInfo>();
                HasError = true;
            }
            finally
            {
                IsLoading = false;
                HasLoaded = true;
                Rebuild();
            }
        }

        /// <summary>Force a refresh (e.g. a pull-to-refresh) ignoring the loaded flag.</summary>
        public Task RefreshAsync() { HasLoaded = false; return LoadAsync(); }

        [RelayCommand]
        private void SelectAuto()
        {
            _session.SelectAuto();
            IsAutoSelected = true;
            SyncSelection();
        }

        [RelayCommand]
        private void SelectServer(ServerRow? row)
        {
            if (row is null) return;
            _session.Select(row.Server);
            IsAutoSelected = false;
            SyncSelection();
        }

        private void Rebuild()
        {
            var q = (SearchQuery ?? string.Empty).Trim().ToLowerInvariant();
            var filtered = _all
                .Where(s => q.Length == 0 ||
                            ($"{s.Name} {s.City} {s.Country}").ToLowerInvariant().Contains(q))
                .ToList();

            // "Recommended" = the two fastest locations (by ping, then load).
            static int PingKey(ServerInfo s) => s.PingMs ?? int.MaxValue;
            var recIds = filtered.OrderBy(PingKey).ThenBy(s => s.CurrentLoad).Take(2).Select(s => s.Id).ToHashSet();

            Recommended.Clear();
            AllServers.Clear();
            foreach (var s in filtered.OrderBy(PingKey).ThenBy(s => s.CurrentLoad))
            {
                var row = new ServerRow(s) { IsSelected = IsSelectedServer(s) };
                if (recIds.Contains(s.Id)) Recommended.Add(row);
                else AllServers.Add(row);
            }

            OnPropertyChanged(nameof(HasRecommended));
            OnPropertyChanged(nameof(HasAll));
            OnPropertyChanged(nameof(NoResults));
            OnPropertyChanged(nameof(LocationCount));
            OnPropertyChanged(nameof(LocationCountText));
        }

        private bool IsSelectedServer(ServerInfo s) =>
            !_session.IsAutoSelect && _session.SelectedServer?.Id == s.Id;

        private void SyncSelection()
        {
            IsAutoSelected = _session.IsAutoSelect;
            foreach (var r in Recommended) r.IsSelected = IsSelectedServer(r.Server);
            foreach (var r in AllServers) r.IsSelected = IsSelectedServer(r.Server);
        }
    }

    /// <summary>Display wrapper for one server row (highlightable selection).</summary>
    public partial class ServerRow : ObservableObject
    {
        public ServerInfo Server { get; }

        [ObservableProperty] private bool _isSelected;

        public ServerRow(ServerInfo server) => Server = server;

        public string Name => string.IsNullOrWhiteSpace(Server.Name) ? Server.Country : Server.Name;
        public string City => Server.City;
        public int Load => Server.CurrentLoad;

        /// <summary>
        /// Occupancy as a percentage of capacity.
        ///
        /// <para>Measured on <c>reserved_count</c>, not on who is online: reserved is what
        /// the API checks before letting anyone bind, so it is what decides whether this
        /// node can be picked at all. A node can read 90% here with nobody connected.</para>
        /// </summary>
        public int LoadPercent => Server.MaxClients > 0
            ? Math.Clamp(Server.ReservedCount * 100 / Server.MaxClients, 0, 100)
            : 0;

        public string SubText => string.IsNullOrWhiteSpace(City)
            ? $"загрузка {LoadPercent}%"
            : $"{City} · загрузка {LoadPercent}%";

        // Latency, measured by LatencyProbe when this screen loads. Null means the node
        // did not answer within the probe budget — which is a different fact from "slow",
        // so the pill is hidden rather than showing a made-up large number.
        public int? Ping => Server.PingMs;
        public bool HasPing => Server.PingMs.HasValue;
        public string PingText => Server.PingMs is { } p ? $"{p} мс" : "";
        public Color PingColor => PingColorFor(Server.PingMs);

        internal static Color PingColorFor(int? ping) => ping switch
        {
            null => Color.FromArgb("#73EFEAF6"),
            < 40 => Color.FromArgb("#7ED9A7"),
            < 80 => Color.FromArgb("#F3D48E"),
            _ => Color.FromArgb("#F2A6A0"),
        };

        /// <summary>Two-letter chip derived from the country name (best-effort placeholder).</summary>
        public string Code
        {
            get
            {
                var src = string.IsNullOrWhiteSpace(Server.Country) ? Server.Name : Server.Country;
                var letters = new string(src.Where(char.IsLetter).Take(2).ToArray());
                return letters.ToUpperInvariant();
            }
        }

        /// <summary>Green/amber/red by real server load (stands in for a latency indicator).</summary>
        public Color QualityColor => Load < 40
            ? Color.FromArgb("#7ED9A7")
            : Load < 70 ? Color.FromArgb("#F3D48E") : Color.FromArgb("#F2A6A0");
    }
}
