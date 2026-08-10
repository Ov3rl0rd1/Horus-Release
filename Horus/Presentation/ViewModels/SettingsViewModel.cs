using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using Horus.Presentation.Navigation;
using Horus.Protocols;

namespace Horus.Presentation.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly IAuthService _auth;
        private readonly IGeoDataService _geo;
        private readonly IRoutingService _routing;
        private readonly ISplitTunnelingService _splitTunneling;
        private readonly IErrorReportingService _errorReporting;
        private readonly Navigator _nav;
        private readonly AuthFlowViewModel _authFlow;
        private readonly PaymentViewModel _payment;

        // ── Account ──────────────────────────────────────────────────────────
        [ObservableProperty] private string _username = string.Empty;
        [ObservableProperty] private string _planBadge = "Free";
        [ObservableProperty] private string _renewalDate = "—";
        [ObservableProperty] private string _accountEmail = "—";
        [ObservableProperty] private string _subscriptionValue = "не активна";

        // ── About ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Display version plus build number. The build number is what makes a bug report
        /// traceable — without it "are you on the latest build?" is unanswerable and you
        /// end up chasing an already-fixed bug.
        /// </summary>
        public string AppVersion => $"{AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";

        public string SupportHandle => AppConfiguration.SupportHandle;

        // ── Connection toggles (placeholders — not yet enforced) ──
        [ObservableProperty] private bool _autoStart = true;
        public string SplitTunnelingValue =>
            SplitTunnelingMode == SplitTunnelingMode.Disabled ? "выкл" : "включён";

        // ── GeoIP ────────────────────────────────────────────────────────────
        [ObservableProperty] private string _geoDbStatus = "Not loaded";
        [ObservableProperty] private bool _isUpdatingGeoDb;
        [ObservableProperty] private bool _ruBypassEnabled = true;

        // ── Routing rules ─────────────────────────────────────────────────────
        [ObservableProperty] private string _routingRulesStatus = "Not fetched";
        [ObservableProperty] private bool _isUpdatingRules;

        // ── Split tunneling ───────────────────────────────────────────────────
        [ObservableProperty] private bool _splitTunnelingSupported;
        [ObservableProperty] private SplitTunnelingMode _splitTunnelingMode = SplitTunnelingMode.Disabled;
        [ObservableProperty] private IReadOnlyList<AppOrProcessEntry> _availableApps = [];
        [ObservableProperty] private bool _isLoadingApps;

        /// <summary>Observable rows for the Split tunneling screen.</summary>
        public ObservableCollection<SplitAppRow> SplitApps { get; } = new();

        // "Все — через VPN" == Disabled; "Выбранные — напрямую" == Blacklist (selected bypass).
        public bool IsModeAll => SplitTunnelingMode == SplitTunnelingMode.Disabled;
        public bool IsModeCustom => !IsModeAll;
        public double AppsOpacity => IsModeCustom ? 1.0 : 0.4;

        [RelayCommand] private void SetModeAll() => SplitTunnelingMode = SplitTunnelingMode.Disabled;
        [RelayCommand] private void SetModeCustom() => SplitTunnelingMode = SplitTunnelingMode.Blacklist;

        // ── Connection ────────────────────────────────────────────────────────
        [ObservableProperty] private bool _autoConnect;
        [ObservableProperty] private bool _killSwitch;
        [ObservableProperty] private string _customDns = "1.1.1.1, 8.8.8.8";

        // ── VPN core (xray) ───────────────────────────────────────────────────
        [ObservableProperty] private string _coreVersion = "—";

        // ── Status ────────────────────────────────────────────────────────────
        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private string _statusMessage = string.Empty;

        public SettingsViewModel(
            IAuthService auth,
            IGeoDataService geo,
            IRoutingService routing,
            ISplitTunnelingService splitTunneling,
            IErrorReportingService errorReporting,
            Navigator nav,
            AuthFlowViewModel authFlow,
            PaymentViewModel payment)
        {
            _auth = auth;
            _geo = geo;
            _routing = routing;
            _splitTunneling = splitTunneling;
            _errorReporting = errorReporting;
            _nav = nav;
            _authFlow = authFlow;
            _payment = payment;

            SplitTunnelingSupported = splitTunneling.IsSupported;
        }

        public async Task InitializeAsync()
        {
            // Account info
            if (_auth.CurrentUser != null)
            {
                Username = _auth.CurrentUser.username;
                AccountEmail = _auth.CurrentUser.email ?? _auth.CurrentUser.username;

                var expiry = _auth.CurrentUser.expiresAt;
                RenewalDate = expiry.HasValue ? expiry.Value.ToLocalTime().ToString("d MMM yyyy") : "—";

                // "не активна" only once the server has confirmed it — otherwise a cold
                // start would claim the subscription is gone before /whoami has answered.
                SubscriptionValue = expiry.HasValue
                    ? $"до {expiry.Value.ToLocalTime():d MMM}"
                    : _auth.SubscriptionState == SubscriptionState.Unknown ? "проверяем…" : "не активна";
            }

            // GeoIP status
            RefreshGeoStatus();

            // Split tunneling
            SplitTunnelingMode = _splitTunneling.Mode;

            // Version string straight from the linked core. This one value separates
            // "library missing from the package" from "library present but failing".
            CoreVersion = XrayProtocol.CoreVersion;

            await Task.CompletedTask;
        }

        // ── GeoIP ────────────────────────────────────────────────────────────

        [RelayCommand]
        async Task UpdateGeoDbAsync()
        {
            if (IsUpdatingGeoDb) return;
            IsUpdatingGeoDb = true;
            try
            {
                await _geo.UpdateGeoDataAsync(string.Empty, string.Empty);
                RefreshGeoStatus();
                ShowStatus("GeoIP database updated.");
            }
            catch (Exception ex)
            {
                ShowStatus($"GeoIP update failed: {ex.Message}");
            }
            finally
            {
                IsUpdatingGeoDb = false;
            }
        }

        // ── Routing rules ─────────────────────────────────────────────────────

        [RelayCommand]
        async Task RefreshRoutingRulesAsync()
        {
            if (IsUpdatingRules) return;
            IsUpdatingRules = true;
            try
            {
                await _routing.RefreshFromServerAsync();
                RoutingRulesStatus = $"Updated: {DateTime.Now:HH:mm}";
                ShowStatus("Routing rules refreshed.");
            }
            catch (Exception ex)
            {
                ShowStatus($"Failed: {ex.Message}");
            }
            finally
            {
                IsUpdatingRules = false;
            }
        }

        // ── Split tunneling ───────────────────────────────────────────────────

        private static readonly string[] ChipPalette =
            { "#5B8DEF", "#E05656", "#4CAF7D", "#4E9EC9", "#7B61C9", "#D98E3E" };

        /// <summary>Every user-togglable row, unfiltered. The source the search filters over.</summary>
        private List<SplitAppRow> _allApps = [];

        private CancellationTokenSource? _iconCts;
        private CancellationTokenSource? _searchCts;

        /// <summary>
        /// Rows currently shown. A plain list, replaced wholesale — mutating an
        /// ObservableCollection of ~200 rows fires a notification per item and makes both
        /// typing and clearing the search box visibly stutter.
        /// </summary>
        [ObservableProperty] private IReadOnlyList<SplitAppRow> _visibleApps = [];

        /// <summary>Config-forced bypass rows, shown above the list and not togglable.</summary>
        [ObservableProperty] private IReadOnlyList<SplitAppRow> _blockedApps = [];

        [ObservableProperty] private bool _isBlockedExpanded;
        [ObservableProperty] private string _appSearch = string.Empty;

        /// <summary>First letters present in the visible list, for the A–Z jump strip.</summary>
        [ObservableProperty] private IReadOnlyList<string> _alphabetIndex = [];

        public bool HasBlockedApps => BlockedApps.Count > 0;
        public string BlockedSummary => $"{BlockedApps.Count} прил. · всегда напрямую";
        public string BlockedChevron => IsBlockedExpanded ? "⌄" : "›";
        public bool NoAppResults => !IsLoadingApps && VisibleApps.Count == 0 && _allApps.Count > 0;

        partial void OnBlockedAppsChanged(IReadOnlyList<SplitAppRow> value)
        {
            OnPropertyChanged(nameof(HasBlockedApps));
            OnPropertyChanged(nameof(BlockedSummary));
        }

        partial void OnIsBlockedExpandedChanged(bool value) => OnPropertyChanged(nameof(BlockedChevron));

        partial void OnVisibleAppsChanged(IReadOnlyList<SplitAppRow> value) =>
            OnPropertyChanged(nameof(NoAppResults));

        /// <summary>
        /// Debounced so a fast typist filters once, not once per keystroke. Clearing the
        /// box cancels any pending filter rather than queueing another pass over the list.
        /// </summary>
        partial void OnAppSearchChanged(string value)
        {
            _searchCts?.Cancel();
            var cts = new CancellationTokenSource();
            _searchCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(160, cts.Token);
                    var filtered = Filter(value);
                    if (cts.Token.IsCancellationRequested) return;

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (cts.Token.IsCancellationRequested) return;
                        VisibleApps = filtered;
                        AlphabetIndex = BuildIndex(filtered);
                    });
                }
                catch (OperationCanceledException) { }
            }, cts.Token);
        }

        private List<SplitAppRow> Filter(string query)
        {
            query = query.Trim();
            if (query.Length == 0) return _allApps;

            // Matches the package name too: users looking for a specific app often know
            // the id from a forum post rather than the display name.
            return [.. _allApps.Where(r =>
                r.SearchName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || r.Id.Contains(query, StringComparison.OrdinalIgnoreCase))];
        }

        private static IReadOnlyList<string> BuildIndex(IReadOnlyList<SplitAppRow> rows) =>
            [.. rows.Select(r => r.IndexKey).Distinct().Take(28)];

        /// <summary>Index of the first row under a letter, for the jump strip. -1 if none.</summary>
        public int IndexOfLetter(string letter)
        {
            for (int i = 0; i < VisibleApps.Count; i++)
                if (VisibleApps[i].IndexKey == letter) return i;
            return -1;
        }

        [RelayCommand]
        private void ToggleBlocked() => IsBlockedExpanded = !IsBlockedExpanded;

        [RelayCommand]
        async Task LoadAppsAsync()
        {
            if (IsLoadingApps) return;
            IsLoadingApps = true;
            try
            {
                var entries = await _splitTunneling.GetAvailableEntriesAsync();
                AvailableApps = entries;

                var forced = _splitTunneling.AlwaysDirectEntries.ToHashSet(StringComparer.OrdinalIgnoreCase);

                var user = new List<SplitAppRow>(entries.Count);
                var blocked = new List<SplitAppRow>();
                int i = 0;

                foreach (var app in entries)
                {
                    var row = new SplitAppRow(
                        app.Id, app.DisplayName,
                        Color.FromArgb(ChipPalette[i++ % ChipPalette.Length]),
                        isDirect: forced.Contains(app.Id) || IsAppSelected(app.Id),
                        isLocked: forced.Contains(app.Id))
                    { IconPath = app.IconPath };

                    (row.IsLocked ? blocked : user).Add(row);
                }

                _allApps = user;
                BlockedApps = blocked;
                VisibleApps = Filter(AppSearch);
                AlphabetIndex = BuildIndex(VisibleApps);

                StartIconLoad(entries);
            }
            finally
            {
                IsLoadingApps = false;
                OnPropertyChanged(nameof(NoAppResults));
            }
        }

        /// <summary>
        /// Icons stream in after the list is already on screen. Decoding another package's
        /// drawable is far too slow to block the list on — several seconds across a couple
        /// of hundred apps — and every icon is cached to disk, so this only really runs
        /// once per install.
        /// </summary>
        private void StartIconLoad(IReadOnlyList<AppOrProcessEntry> entries)
        {
            _iconCts?.Cancel();
            var cts = new CancellationTokenSource();
            _iconCts = cts;

            var byId = _allApps.Concat(BlockedApps)
                .ToDictionary(r => r.Id, StringComparer.Ordinal);

            _ = _splitTunneling.LoadIconsAsync(entries, entry =>
            {
                if (cts.Token.IsCancellationRequested) return;
                if (!byId.TryGetValue(entry.Id, out var row)) return;

                MainThread.BeginInvokeOnMainThread(() => row.IconPath = entry.IconPath);
            }, cts.Token);
        }

        /// <summary>
        /// Persists a single app's direct/VPN choice. The row's <c>IsDirect</c> is
        /// already flipped by the two-way Switch binding; here we sync the service.
        /// </summary>
        [RelayCommand]
        async Task ApplyApp(SplitAppRow? row)
        {
            // Locked rows are forced direct by configuration; a stray toggle must not
            // persist them into the user's selection.
            if (row is null || row.IsLocked) return;

            var current = _splitTunneling.SelectedEntries.ToHashSet();
            if (row.IsDirect) current.Add(row.Id);
            else current.Remove(row.Id);

            await _splitTunneling.SetSelectedEntriesAsync(current);
            await _splitTunneling.ApplyAsync();
        }

        partial void OnSplitTunnelingModeChanged(SplitTunnelingMode value)
        {
            _splitTunneling.Mode = value;
            OnPropertyChanged(nameof(SplitTunnelingValue));
            OnPropertyChanged(nameof(IsModeAll));
            OnPropertyChanged(nameof(IsModeCustom));
            OnPropertyChanged(nameof(AppsOpacity));
        }

        // ── Navigation (custom root, no Shell) ──
        [RelayCommand] private void GoSplit() => _nav.Go(AppScreen.Split);
        [RelayCommand] private void BackToSettings() => _nav.Go(AppScreen.Settings);
        [RelayCommand] private void OpenPay() => _payment.Open();

        // ── Error reporting ───────────────────────────────────────────────────

        /// <summary>
        /// Builds the diagnostics archive and hands it straight to the user — share sheet
        /// on Android, Explorer on Windows. Nothing is uploaded; the API has no ingest
        /// endpoint, so the archive only helps if the user can actually send it.
        /// </summary>
        [RelayCommand]
        async Task CollectLogsAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                ShowStatus("Собираем логи…");
                var path = await _errorReporting.BuildArchiveAsync();

                if (await _errorReporting.ShareArchiveAsync(path))
                {
                    ShowStatus($"Архив готов: {Path.GetFileName(path)}");
                }
                else
                {
                    await Dialog.Alert("Архив сохранён",
                        $"Не удалось открыть меню отправки. Файл лежит здесь:\n\n{path}");
                }
            }
            catch (Exception ex)
            {
                await Dialog.Alert("Не удалось собрать логи", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>Rolling connection log, for testers who would rather paste text than
        /// wrestle with a file picker.</summary>
        public string SessionLogText
        {
            get
            {
                var lines = _errorReporting.SessionLog;
                return lines.Count == 0
                    ? "Журнал пуст — подключитесь, чтобы он заполнился."
                    : string.Join(Environment.NewLine, lines);
            }
        }

        /// <summary>
        /// Shows the tail of the connection log with a one-tap copy. Testers who won't
        /// wrestle with a file picker will paste text into a chat, so this is the path
        /// that actually produces diagnostics for most of them.
        /// </summary>
        [RelayCommand]
        private async Task ShowSessionLogAsync()
        {
            OnPropertyChanged(nameof(SessionLogText));

            var full = SessionLogText;
            var tail = string.Join(Environment.NewLine,
                full.Split(Environment.NewLine).TakeLast(25));

            var copy = await Dialog.Confirm("Журнал подключения", tail, "Копировать", "Закрыть");
            if (!copy) return;

            await Clipboard.Default.SetTextAsync(full);
            ShowStatus("Журнал скопирован.");
        }

        // ── Account ───────────────────────────────────────────────────────────

        [RelayCommand]
        async Task SignOutAsync()
        {
            var confirm = await Dialog.Confirm(
                "Выйти из аккаунта", "Вы уверены, что хотите выйти?", "Выйти", "Отмена");
            if (!confirm) return;

            await _auth.LogoutAsync();
            _authFlow.Reset();
            _nav.Reset(AppScreen.Login);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private void RefreshGeoStatus()
        {
            if (_geo.IsGeoIpLoaded)
                GeoDbStatus = $"Loaded — {_geo.GeoIpLastUpdated?.ToLocalTime().ToString("d MMM yyyy") ?? "unknown date"}";
            else
                GeoDbStatus = "Not loaded";
        }

        private void ShowStatus(string message)
        {
            StatusMessage = message;
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                await Task.Delay(4000);
                if (StatusMessage == message) StatusMessage = string.Empty;
            });
        }

        public bool IsAppSelected(string appId) =>
            _splitTunneling.SelectedEntries.Contains(appId);
    }

    /// <summary>Display row for one app in the Split tunneling screen.</summary>
    public partial class SplitAppRow : ObservableObject
    {
        [ObservableProperty] private bool _isDirect;

        /// <summary>Cached PNG path, filled in after the list renders. Null until then.</summary>
        [ObservableProperty] private string? _iconPath;

        public string Id { get; }
        public string Name { get; }
        public Color ChipColor { get; }

        /// <summary>Forced direct by configuration — shown, but not togglable.</summary>
        public bool IsLocked { get; }

        /// <summary>Lower-cased once at construction; the search filter runs over this.</summary>
        public string SearchName { get; }

        public string Letter => string.IsNullOrEmpty(Name) ? "#" : Name[..1].ToUpperInvariant();

        /// <summary>
        /// Bucket for the A–Z strip. Anything not starting with a letter collapses into
        /// "#", so digits and punctuation don't each claim their own jump target.
        /// </summary>
        public string IndexKey => Letter.Length == 1 && char.IsLetter(Letter[0]) ? Letter : "#";

        public SplitAppRow(string id, string name, Color chip, bool isDirect, bool isLocked = false)
        {
            Id = id;
            Name = name;
            ChipColor = chip;
            _isDirect = isDirect;
            IsLocked = isLocked;
            SearchName = name ?? string.Empty;
        }

        public bool HasIcon => !string.IsNullOrEmpty(IconPath);

        /// <summary>Letter chip is only shown until the real icon arrives.</summary>
        public bool ShowLetter => !HasIcon;

        public bool CanToggle => !IsLocked;

        public string StatusText => IsLocked
            ? "Всегда напрямую — задано в приложении"
            : IsDirect ? "Напрямую, мимо VPN" : "Через VPN";

        public Color StatusColor => IsDirect ? Color.FromArgb("#F3D48E") : Color.FromArgb("#73EFEAF6");

        partial void OnIsDirectChanged(bool value)
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusColor));
        }

        partial void OnIconPathChanged(string? value)
        {
            OnPropertyChanged(nameof(HasIcon));
            OnPropertyChanged(nameof(ShowLetter));
        }
    }
}
