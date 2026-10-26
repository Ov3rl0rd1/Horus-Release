using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Horus.Application;
using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using Horus.Presentation.Navigation;

namespace Horus.Presentation.ViewModels
{
    /// <summary>
    /// Home screen. Connect/disconnect, live traffic, duration, the public-IP card and
    /// the selected-server card are all wired to the real <see cref="VpnManager"/>,
    /// traffic monitor and <c>GET /whoami</c>. Subscription days-left comes from the
    /// subscription expiry on <see cref="IAuthService.CurrentUser"/>.
    /// </summary>
    public partial class MainViewModel : ObservableObject
    {
        private readonly IVpnController _vpnManager;
        private readonly ITrafficMonitorService _traffic;
        private readonly ISubscriptionService _subscription;
        private readonly IAuthService _auth;
        private readonly IApiService _api;
        private readonly IAccountSync _accountSync;
        private readonly IErrorReportingService _errorReporting;
        private readonly IAccountServer _accountServer;
        private readonly ServerSwitcher _serverSwitcher;
        private readonly Navigator _nav;
        private readonly IPublisherTrustService _publisherTrust;
        private readonly INoticeService _notices;

        [ObservableProperty] private VpnState _vpnState = VpnState.Disconnected;
        [ObservableProperty] private string _downloadSpeed = "—";
        [ObservableProperty] private string _uploadSpeed = "—";
        [ObservableProperty] private string _sessionDuration = "00:00";

        /// <summary>Normalized (0..1) download speed feeding the live speed graph.</summary>
        [ObservableProperty] private double _speedLevel = 0.1;

        /// <summary>Egress IP as the API sees it; null until /whoami answers.</summary>
        [ObservableProperty] private string? _publicIp;

        // ── Publisher trust (Windows; inert everywhere else) ────────────────

        /// <summary>
        /// Shown when the build is signed with a certificate this machine does not trust.
        /// Set once at construction: trust does not change underneath a running process,
        /// and re-checking on every render would read the certificate stores continuously.
        /// </summary>
        [ObservableProperty] private bool _showPublisherWarning;

        [ObservableProperty] private bool _isInstallingCertificate;

        [RelayCommand]
        private async Task InstallCertificateAsync()
        {
            if (IsInstallingCertificate) return;
            IsInstallingCertificate = true;

            try
            {
                if (await _publisherTrust.InstallAsync())
                {
                    ShowPublisherWarning = false;
                    await Dialog.Alert("Сертификат установлен",
                        "Windows больше не считает издателя неизвестным.");
                }
                else
                {
                    await Dialog.Alert("Не удалось",
                        "Сертификат установлен, но подпись всё ещё не проходит проверку. " +
                        "Скорее всего, он просрочен или файл изменён после подписи.");
                }
            }
            catch (Exception ex)
            {
                await Dialog.Alert("Не удалось установить сертификат", ex.Message);
            }
            finally
            {
                IsInstallingCertificate = false;
            }
        }

        public MainViewModel(
            IVpnController vpnManager,
            ITrafficMonitorService traffic,
            ISubscriptionService subscription,
            IAuthService auth,
            IApiService api,
            IAccountSync accountSync,
            IErrorReportingService errorReporting,
            IAccountServer accountServer,
            ServerSwitcher serverSwitcher,
            Navigator nav,
            IPublisherTrustService publisherTrust,
            INoticeService notices)
        {
            _publisherTrust = publisherTrust;
            _showPublisherWarning = publisherTrust.NeedsTrust;

            _vpnManager = vpnManager;
            // The tunnel may be up before this screen exists: started from the Quick Settings
            // tile, or restored after the process died, both with no UI. Starting from
            // "disconnected" showed it off while it ran.
            _vpnState = vpnManager.State;
            _traffic = traffic;
            _subscription = subscription;
            _auth = auth;
            _api = api;
            _accountSync = accountSync;
            _errorReporting = errorReporting;
            _accountServer = accountServer;
            _serverSwitcher = serverSwitcher;
            _nav = nav;
            _notices = notices;

            _notices.Changed += (_, __) => SyncNotices();
            SyncNotices();

            _vpnManager.StateChanged += OnVpnStateChanged;
            _traffic.TrafficUpdated += OnTrafficUpdated;
            _accountServer.Changed += (_, __) => MainThread.BeginInvokeOnMainThread(RaiseServerCard);

            // The poll runs whether or not this screen asked for it, so pick up its result
            // wherever it lands. Without this a subscription granted while the user watches
            // Home updates the model and never reaches the banner.
            _accountSync.AccountRefreshed += (_, __) => ApplyAccountSnapshot();
        }

        // ── Derived connection state ──────────────────────────────────────────
        public bool IsOn => VpnState == VpnState.Connected;
        public bool IsConnecting => VpnState is VpnState.Connecting or VpnState.Reconnecting;
        public bool IsOff => !IsOn && !IsConnecting;

        public string StatusText => IsOn ? "ЗАЩИЩЕНО" : IsConnecting ? "ПОДКЛЮЧЕНИЕ" : "ОТКЛЮЧЕНО";
        public Color StatusColor => IsOn ? Hz("#7ED9A7") : IsConnecting ? Hz("#F3D48E") : Hz("#99EFEAF6");
        public Color PillBg => IsOn ? Hz("#147ED9A7") : IsConnecting ? Hz("#14F0C46A") : Hz("#0DFFFFFF");
        public Color PillBorder => IsOn ? Hz("#4D7ED9A7") : IsConnecting ? Hz("#4DF0C46A") : Hz("#21FFFFFF");
        public string StatusSub => IsOn ? "Ваш трафик зашифрован"
            : IsConnecting ? "Ищем лучший маршрут…" : "Нажмите кнопку, чтобы подключиться";

        /// <summary>
        /// Which outbound is actually carrying traffic, for the caption under the status
        /// pill. Empty unless connected — naming a protocol we aren't using would be worse
        /// than saying nothing.
        ///
        /// The node's own label for the offer in use. Taken from the API rather than mapped
        /// from an enum here: the node decides what it is running and what to call it, so a
        /// protocol this build has never heard of still shows a sensible name instead of
        /// falling through to blank.
        /// </summary>
        public string ActiveProtocolLabel => IsOn
            ? _vpnManager.ActiveOfferLabel ?? string.Empty
            : string.Empty;

        public bool ShowActiveProtocol => ActiveProtocolLabel.Length > 0;

        public double BarsOpacity => IsOn ? 1.0 : 0.35;
        public string BtnLabel => IsOn ? "ОТКЛЮЧИТЬ" : IsConnecting ? "ОТМЕНА" : "ПОДКЛЮЧИТЬ";
        public Color BtnWordColor => IsOn ? Hz("#F3D48E") : IsConnecting ? Hz("#CCF3D48E") : Hz("#99EFEAF6");
        public Color RingBorder => IsOn ? Hz("#99F0C46A") : Hz("#21FFFFFF");
        public double LogoOpacity => IsOn ? 1.0 : IsConnecting ? 0.75 : 0.45;
        public double SpinOpacity => IsConnecting ? 1.0 : 0.0;
        public bool ShowTimer => IsOn;

        // ── IP card (real egress IP from GET /whoami) ──
        public string IpValue => IsOn
            ? "•••.•••.•••.•••"
            : PublicIp ?? "—";
        public string IpNote => IsOn
            ? "Ваш IP скрыт"
            : PublicIp is null ? "IP не определён" : "Ваш IP виден";
        public Color IpColor => IsOn ? Hz("#7ED9A7") : Hz("#F2A6A0");

        partial void OnPublicIpChanged(string? value)
        {
            OnPropertyChanged(nameof(IpValue));
            OnPropertyChanged(nameof(IpNote));
        }

        /// <summary>
        /// Reads the egress IP + subscription state. Safe to call on every Home entry.
        ///
        /// One request, not three: <see cref="IAccountSync"/> owns the <c>/whoami</c> call
        /// and shares the snapshot with the auth and subscription services. This used to
        /// make its own call for the IP and then trigger two more indirectly.
        /// </summary>
        public async Task RefreshAccountAsync()
        {
            await _accountSync.RefreshNowAsync();
            ApplyAccountSnapshot();
        }

        private void ApplyAccountSnapshot()
        {
            if (_accountSync.Last is { } me) PublicIp = me.Ip;
            RaiseAll();
        }

        // ── The account's server card ──
        // One server for every device on the account; none until the first connect picks one.
        private ServerInfo? AccountServer => _accountServer.Current;

        /// <summary>Measured on the servers list, when the account's server is in it.</summary>
        private int? AccountServerPing =>
            QuickServers.FirstOrDefault(r => r.Server.Id == AccountServer?.Id)?.Server.PingMs;

        public string CurCode => AccountServer is { } s ? new ServerRow(s).Code : "—";
        public string CurName => AccountServer?.Name ?? "Сервер не выбран";
        public string CurSub => AccountServer is { } s
            ? (s.City.Length > 0 ? s.Country : string.Empty)
            : "Назначим при подключении";

        public bool ShowCurPing => AccountServerPing is not null;
        public string CurPing => AccountServerPing is { } p ? $"{p} мс" : "";
        public Color CurPingColor => ServerRow.PingColorFor(AccountServerPing);

        // ── Subscription / renew banner (real expiry) ──
        public int SubDaysLeft
        {
            get
            {
                var exp = _auth.CurrentUser?.expiresAt;
                if (exp is null) return 0;
                var days = (int)Math.Ceiling((exp.Value.ToUniversalTime() - DateTime.UtcNow).TotalDays);
                return Math.Max(days, 0);
            }
        }

        /// <summary>True once <c>/whoami</c> has told us where we stand.</summary>
        private bool SubscriptionKnown => _auth.SubscriptionState != SubscriptionState.Unknown;

        // ── Notices ───────────────────────────────────────────────────────────
        //
        // The subscription banner used to be four bespoke properties bound straight into
        // the view. Three more conditions needed the same treatment — an install permission
        // that silently prevents updates, notifications the user switched off, and battery
        // optimisation that ends tunnels overnight — so the view now renders a list and
        // INoticeService decides what is in it. The subscription case is simply one entry.

        public ObservableCollection<AppNotice> Notices { get; } = [];

        public bool HasNotices => Notices.Count > 0;

        private void SyncNotices()
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Notices.Clear();
                foreach (var notice in _notices.Current) Notices.Add(notice);
                OnPropertyChanged(nameof(HasNotices));
            });
        }

        /// <summary>
        /// Runs a notice's action. Subscription opens the site's tariff page, which is
        /// handled here rather than in the service — the Application layer has no business
        /// opening a browser.
        /// </summary>
        [RelayCommand]
        private async Task ActOnNoticeAsync(AppNotice? notice)
        {
            if (notice is null) return;

            if (notice.Kind == NoticeKind.Subscription) { await SubscriptionPage.OpenAsync(); return; }

            await _notices.ActAsync(notice.Kind);
        }

        [RelayCommand]
        private void DismissNotice(AppNotice? notice)
        {
            if (notice is null) return;
            _notices.Dismiss(notice.Kind);
        }

        // ── Commands ──────────────────────────────────────────────────────────

        /// <summary>
        /// Concurrent on purpose: the press that cancels a connect arrives while the press that
        /// started it is still running. Without it the command disabled itself for the whole
        /// connect, and the button "only worked once connected".
        /// </summary>
        [RelayCommand(AllowConcurrentExecutions = true)]
        private async Task ToggleConnectAsync()
        {
            if (ConnectButton.ActionFor(LiveVpnState()) == ConnectButtonAction.Disconnect)
            {
                await DisconnectAsync();
                return;
            }

            // Only block on a subscription the server has actually confirmed is gone.
            // While the state is Unknown we let the attempt through: the API is the
            // authority and answers 403 if it really has expired, which is handled below.
            if (SubscriptionKnown && SubDaysLeft <= 0)
            {
                // Re-check first — access is granted server-side, so a cached expiry can
                // be stale and a just-granted account would otherwise still bounce.
                await RefreshAccountAsync();

                if (SubscriptionKnown && SubDaysLeft <= 0)
                {
                    await SubscriptionPage.OfferAsync();
                    return;
                }
            }

            await ConnectAsync();
        }

        /// <summary>The controller's state now — not what this screen last heard.</summary>
        private VpnState LiveVpnState()
        {
#if DEBUG
            if (AppConfiguration.UseDevBypass) return VpnState;
#endif
            return _vpnManager.State;
        }

        /// <summary>Catches the screen up with the controller, for when the app comes back to the front.</summary>
        public void SyncVpnState()
        {
            if (VpnState == LiveVpnState()) return;
            VpnState = LiveVpnState();
            RaiseAll();
        }

        [RelayCommand] private void GoServers() => _nav.Go(AppScreen.Servers);
        [RelayCommand] private Task OpenPayAsync() => SubscriptionPage.OpenAsync();

        #region DEV-PANEL — pre-release test harness; delete this region with Horus/DevTools/

        /// <summary>Caption showing a pinned outbound. Empty, and hidden, in normal use.</summary>
        public string DevBadge => Horus.DevTools.DevOutbound.Badge;

        /// <summary>Opened by tapping the grey status caption on the home screen.</summary>
        [RelayCommand]
        private async Task DevPanelAsync()
        {
            await Horus.DevTools.DevOutbound.ShowAsync(_vpnManager);
            OnPropertyChanged(nameof(DevBadge));
        }

        #endregion

        // ── Recommended quick-connect list (Windows desktop Home right column) ──
        public ObservableCollection<ServerRow> QuickServers { get; } = new();
        public bool HasQuick => QuickServers.Count > 0;

        public async Task LoadQuickServersAsync()
        {
            try
            {
                var servers = await _subscription.GetAvailableServersAsync();
                var top = (servers ?? Array.Empty<ServerInfo>())
                    .OrderBy(s => s.PingMs ?? int.MaxValue).ThenBy(s => s.CurrentLoad).Take(3).ToList();
                QuickServers.Clear();
                foreach (var s in top) QuickServers.Add(new ServerRow(s));
                OnPropertyChanged(nameof(HasQuick));
                RaiseServerCard(); // the account server's ping comes from this list
            }
            catch { /* leave list as-is */ }
        }

        [RelayCommand]
        private async Task SelectQuickAsync(ServerRow? row)
        {
            if (row is not null) await _serverSwitcher.SwitchToAsync(row.Server);
        }

        [RelayCommand]
        private void Refresh()
        {
            // Re-read persisted subscription state and refresh the banner.
            RaiseAll();
        }

        private async Task ConnectAsync()
        {
#if DEBUG
            if (AppConfiguration.UseDevBypass)
            {
                await SimulateConnectAsync();
                return;
            }
#endif
            try
            {
                // To the account's server; the API picks one for an account that has none.
                await _vpnManager.ConnectAsync();
            }
            catch (OperationCanceledException)
            {
                // Cancelled by a second press: the user's own decision, not a failure.
            }
            catch (SubscriptionExpiredException)
            {
                // The server is the authority on this, and the local gate is deliberately
                // optimistic — so this is the normal way an expired account is discovered.
                await RefreshAccountAsync();
                await SubscriptionPage.OfferAsync();
            }
            catch (UnauthorizedAccessException)
            {
                // HttpAuthHandler already raised SessionExpired; ShellViewModel is routing
                // to Login, so an error dialog on top of that would just be noise.
            }
            catch (Exception ex)
            {
                // Offer the archive and a way to reach us right here — someone who has to go
                // hunting through Settings after a failure mostly just doesn't.
                const string logs = "Собрать логи";
                const string support = "Написать в поддержку";
                var choice = await Dialog.ActionSheet(
                    $"Не удалось подключиться\n\n{ex.Message}", "Закрыть", null,
                    SupportChannels.Configured.Count > 0 ? [support, logs] : [logs]);

                if (choice == logs) await CollectLogsAsync();
                else if (choice == support) await SupportPage.ChooseAsync();
            }
        }

        /// <summary>Builds the diagnostics archive and hands it straight to the user.</summary>
        private async Task CollectLogsAsync()
        {
            try
            {
                var path = await _errorReporting.BuildArchiveAsync();
                if (!await _errorReporting.ShareArchiveAsync(path))
                    await Dialog.Alert("Архив сохранён", path);
            }
            catch (Exception ex)
            {
                await Dialog.Alert("Не удалось собрать логи", ex.Message);
            }
        }

        private async Task DisconnectAsync()
        {
#if DEBUG
            if (AppConfiguration.UseDevBypass)
            {
                SimulateDisconnect();
                return;
            }
#endif
            try { await _vpnManager.DisconnectAsync(); }
            catch (Exception ex) { await Dialog.Alert("Ошибка отключения", ex.Message); }
        }

#if DEBUG
        private CancellationTokenSource? _simCts;

        private async Task SimulateConnectAsync()
        {
            VpnState = VpnState.Connecting;
            RaiseAll();
            await Task.Delay(1200);
            if (VpnState != VpnState.Connecting) return; // toggled off mid-connect
            VpnState = VpnState.Connected;
            RaiseAll();

            _simCts = new CancellationTokenSource();
            _ = SimulateTrafficAsync(_simCts.Token);
        }

        private async Task SimulateTrafficAsync(CancellationToken ct)
        {
            var start = DateTime.UtcNow;
            var rng = new Random();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var d = DateTime.UtcNow - start;
                    SessionDuration = d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"mm\:ss");
                    var dl = 34 + rng.NextDouble() * 80;
                    DownloadSpeed = $"{dl:F1} Мбит/с";
                    UploadSpeed = $"{8 + rng.NextDouble() * 24:F1} Мбит/с";
                    SpeedLevel = Math.Clamp(dl / 120.0, 0.05, 1.0);
                    await Task.Delay(1000, ct);
                }
            }
            catch (TaskCanceledException) { }
        }

        private void SimulateDisconnect()
        {
            _simCts?.Cancel();
            VpnState = VpnState.Disconnected;
            DownloadSpeed = "—";
            UploadSpeed = "—";
            SessionDuration = "00:00";
            RaiseAll();
        }
#endif

        // ── Event handlers ──
        private void OnVpnStateChanged(object? sender, VpnStateChangedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                VpnState = e.NewState;
                if (e.NewState == VpnState.Disconnected)
                {
                    DownloadSpeed = "—";
                    UploadSpeed = "—";
                    SessionDuration = "00:00";
                }
                RaiseAll();
            });
        }

        private void OnTrafficUpdated(object? sender, TrafficUpdatedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var s = e.Stats;
                DownloadSpeed = FormatSpeed(s.SpeedDownBps);
                UploadSpeed = FormatSpeed(s.SpeedUpBps);
                // Normalised against ~100 Mbit/s (12.5 MB/s) for the speed graph.
                SpeedLevel = Math.Clamp(s.SpeedDownBps / 12_500_000.0, 0.05, 1.0);
                var d = s.SessionDuration;
                SessionDuration = d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"mm\:ss");
            });
        }

        private void RaiseServerCard()
        {
            OnPropertyChanged(nameof(CurCode));
            OnPropertyChanged(nameof(CurName));
            OnPropertyChanged(nameof(CurSub));
            OnPropertyChanged(nameof(ShowCurPing));
            OnPropertyChanged(nameof(CurPing));
            OnPropertyChanged(nameof(CurPingColor));
        }

        /// <summary>Refresh every computed binding (state + account + server card).</summary>
        private void RaiseAll() => OnPropertyChanged(string.Empty);

        private static Color Hz(string hex) => Color.FromArgb(hex);

        /// <summary>The counters are bytes per second; the label is bits, hence the ×8.</summary>
        private static string FormatSpeed(long bytesPerSecond)
        {
            var bitsPerSecond = bytesPerSecond * 8.0;
            var mbps = bitsPerSecond / 1_000_000.0;
            return mbps >= 1 ? $"{mbps:F1} Мбит/с" : $"{bitsPerSecond / 1_000.0:F0} Кбит/с";
        }
    }
}
