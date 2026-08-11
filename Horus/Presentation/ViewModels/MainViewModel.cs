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
        private readonly VpnManager _vpnManager;
        private readonly ITrafficMonitorService _traffic;
        private readonly ISubscriptionService _subscription;
        private readonly IAuthService _auth;
        private readonly IApiService _api;
        private readonly IErrorReportingService _errorReporting;
        private readonly AppSession _session;
        private readonly Navigator _nav;
        private readonly PaymentViewModel _payment;
        private readonly IPublisherTrustService _publisherTrust;

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
            VpnManager vpnManager,
            ITrafficMonitorService traffic,
            ISubscriptionService subscription,
            IAuthService auth,
            IApiService api,
            IErrorReportingService errorReporting,
            AppSession session,
            Navigator nav,
            PaymentViewModel payment,
            IPublisherTrustService publisherTrust)
        {
            _publisherTrust = publisherTrust;
            _showPublisherWarning = publisherTrust.NeedsTrust;

            _vpnManager = vpnManager;
            _traffic = traffic;
            _subscription = subscription;
            _auth = auth;
            _api = api;
            _errorReporting = errorReporting;
            _session = session;
            _nav = nav;
            _payment = payment;

            _vpnManager.StateChanged += OnVpnStateChanged;
            _traffic.TrafficUpdated += OnTrafficUpdated;
            _session.PropertyChanged += (_, __) => RaiseServerCard();
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
        /// </summary>
        public string ActiveProtocolLabel => IsOn
            ? _vpnManager.ActiveProtocolType switch
            {
                ProtocolType.Hysteria2 => "Hysteria2",
                ProtocolType.Vless => "VLESS",
                ProtocolType.OlcRtc => "olcRTC",
                _ => string.Empty
            }
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

        /// <summary>Reads the egress IP + subscription state. Safe to call on every Home entry.</summary>
        public async Task RefreshAccountAsync()
        {
            try
            {
                var me = await _api.GetWhoAmIAsync();
                if (me is not null) PublicIp = me.Ip;

                await _subscription.CheckSubscriptionAsync();
                await _auth.RefreshAccountAsync();
                RaiseAll();
            }
            catch { /* offline — keep the last known values */ }
        }

        // ── Selected-server card ──
        // In Auto mode the "display" server is the fastest known one (first quick server).
        private ServerInfo? DisplayServer => _session.IsAutoSelect
            ? QuickServers.FirstOrDefault()?.Server
            : _session.SelectedServer;

        public bool CurIsAuto => _session.IsAutoSelect;
        public string CurCode => _session.SelectedServer is { } s ? new ServerRow(s).Code : "AUTO";
        public string CurName => CurIsAuto ? "Автовыбор" : (_session.SelectedServer?.Name ?? "Автовыбор");
        public string CurSub => CurIsAuto
            ? (IsOn && DisplayServer is { City: { Length: > 0 } c } ? $"{c} · оптимальный сервер" : "Выберет самый быстрый сервер")
            : (_session.SelectedServer?.City ?? "");

        public bool ShowCurPing => (IsOn || !CurIsAuto) && DisplayServer?.PingMs is not null;
        public string CurPing => DisplayServer?.PingMs is { } p ? $"{p} мс" : "";
        public Color CurPingColor => ServerRow.PingColorFor(DisplayServer?.PingMs);

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

        /// <summary>
        /// Hidden until the server has actually said the subscription is short or gone —
        /// otherwise every cold start flashed "Подписка не активна" before /whoami answered.
        /// </summary>
        public bool ShowRenew => SubscriptionKnown && SubDaysLeft <= 7;
        public string RenewTitle => SubDaysLeft > 0 ? "Подписка заканчивается" : "Подписка не активна";
        public string RenewSub => SubDaysLeft > 0
            ? $"Осталось {SubDaysLeft} дн. — продлите, чтобы не потерять доступ"
            : "Оформите подписку, чтобы включить защиту";
        public string RenewLabel => SubDaysLeft > 0 ? "Продлить" : "Оформить";

        // ── Commands ──────────────────────────────────────────────────────────
        [RelayCommand]
        private async Task ToggleConnectAsync()
        {
            if (IsOn || IsConnecting) { await DisconnectAsync(); return; }

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
                    _payment.Open();
                    return;
                }
            }

            await ConnectAsync();
        }

        [RelayCommand] private void GoServers() => _nav.Go(AppScreen.Servers);
        [RelayCommand] private void OpenPay() => _payment.Open();

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
                RaiseServerCard(); // DisplayServer (Auto) depends on QuickServers
            }
            catch { /* leave list as-is */ }
        }

        [RelayCommand]
        private void SelectQuick(ServerRow? row)
        {
            if (row is null) return;
            _session.Select(row.Server);
            RaiseServerCard();
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
                // The API picks and binds the server itself (GET /servers/connect takes no
                // id); the resolved server is passed only so the UI can label the session.
                await _vpnManager.ConnectAsync(await ResolveServerAsync());
            }
            catch (SubscriptionExpiredException)
            {
                // The server is the authority on this, and the local gate is deliberately
                // optimistic — so this is the normal way an expired account is discovered.
                await RefreshAccountAsync();
                _payment.Open();
            }
            catch (UnauthorizedAccessException)
            {
                // HttpAuthHandler already raised SessionExpired; ShellViewModel is routing
                // to Login, so an error dialog on top of that would just be noise.
            }
            catch (Exception ex)
            {
                // Offer the archive right here — a tester who has to go hunting through
                // Settings after a failure mostly just doesn't.
                var collect = await Dialog.Confirm(
                    "Не удалось подключиться", ex.Message, "Собрать логи", "Закрыть");

                if (collect) await CollectLogsAsync();
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

        /// <summary>Auto mode → least-loaded server; otherwise the chosen one. Display only.</summary>
        private async Task<ServerInfo?> ResolveServerAsync()
        {
            if (!_session.IsAutoSelect && _session.SelectedServer is { } chosen)
                return chosen;

            try
            {
                var servers = await _subscription.GetAvailableServersAsync();
                return servers?.OrderBy(s => s.CurrentLoad).FirstOrDefault();
            }
            catch { return null; }
        }

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
            OnPropertyChanged(nameof(CurIsAuto));
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
