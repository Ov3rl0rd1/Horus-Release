#if ADMIN_MODE
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Horus.Application;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using Horus.Protocols;

namespace Horus.Presentation.ViewModels
{
    /// <summary>
    /// Admin panel ViewModel — compiled only when ADMIN_MODE is defined.
    /// Supports:
    ///   - Local mode toggle (manual config without API)
    ///   - Local server CRUD
    ///   - Protocol config override for direct connect
    ///   - Live log viewer
    /// </summary>
    public partial class AdminViewModel : ObservableObject
    {
        private readonly VpnManager _vpnManager;
        private readonly ProtocolFactory _protocolFactory;
        private readonly IErrorReportingService _errorReporting;
        private readonly ILocalModeService _localMode;
        private readonly ILocalConfigService _localConfig;

        // ── Local mode state ────────────────────────────────────────────────

        [ObservableProperty] private bool _isLocalMode;
        [ObservableProperty] private string _localModeLabel = string.Empty;
        [ObservableProperty] private string _apiStatusLabel = string.Empty;

        // ── Local server list ───────────────────────────────────────────────

        [ObservableProperty] private IReadOnlyList<LocalServerEntry> _localServers = [];
        [ObservableProperty] private LocalServerEntry? _selectedServer;

        // Server edit form
        [ObservableProperty] private string _editName = string.Empty;
        [ObservableProperty] private string _editHost = string.Empty;
        [ObservableProperty] private string _editPort = "443";
        [ObservableProperty] private string _editProtocol = "Hysteria2";
        [ObservableProperty] private string _editAuth = string.Empty;
        [ObservableProperty] private string _editObfsType = string.Empty;
        [ObservableProperty] private string _editObfsPassword = string.Empty;
        [ObservableProperty] private string _editPortsRange = string.Empty;
        [ObservableProperty] private bool _editLazyTls;
        [ObservableProperty] private string _editRawConfig = string.Empty;
        [ObservableProperty] private bool _isEditing;
        [ObservableProperty] private bool _isNewEntry;

        // ── Direct connect override ─────────────────────────────────────────

        [ObservableProperty] private string _selectedProtocol = "Hysteria2";
        [ObservableProperty] private string _rawConfig = string.Empty;
        [ObservableProperty] private string _serverHost = string.Empty;
        [ObservableProperty] private string _serverPort = "443";
        [ObservableProperty] private string _authToken = string.Empty;
        [ObservableProperty] private string _obfsType = "salamander";
        [ObservableProperty] private string _obfsPassword = string.Empty;
        [ObservableProperty] private string _portsRange = string.Empty;
        [ObservableProperty] private bool _lazyTls;

        // ── Log viewer ──────────────────────────────────────────────────────

        [ObservableProperty] private string _logText = string.Empty;
        [ObservableProperty] private bool _isAutoScroll = true;

        // ── Status ──────────────────────────────────────────────────────────

        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private string _statusMessage = string.Empty;

        public IReadOnlyList<string> AvailableProtocols { get; } = ["Hysteria2", "OlcRtc"];

        public AdminViewModel(
            VpnManager vpnManager,
            ProtocolFactory protocolFactory,
            IErrorReportingService errorReporting,
            ILocalModeService localMode,
            ILocalConfigService localConfig)
        {
            _vpnManager = vpnManager;
            _protocolFactory = protocolFactory;
            _errorReporting = errorReporting;
            _localMode = localMode;
            _localConfig = localConfig;

            _vpnManager.StateChanged += (_, e) =>
                MainThread.BeginInvokeOnMainThread(() =>
                    AppendLog($"[STATE] {e.OldState} → {e.NewState}{(e.Reason != null ? $" ({e.Reason})" : "")}"));

            // Live protocol output (hysteria2 stdout + stderr)
            _vpnManager.ProtocolOutputReceived += (_, line) =>
                MainThread.BeginInvokeOnMainThread(() => AppendLog(line));

            _localMode.LocalModeChanged += (_, isLocal) =>
                MainThread.BeginInvokeOnMainThread(() => RefreshLocalModeStatus(isLocal));
        }

        public async Task InitializeAsync()
        {
            await _localConfig.LoadAsync();
            RefreshServerList();
            RefreshLocalModeStatus(_localMode.IsLocalMode);
        }

        // ── Local mode ──────────────────────────────────────────────────────

        [RelayCommand]
        async Task ToggleLocalModeAsync()
        {
            var target = !_localMode.IsLocalMode;
            _localMode.SetLocalMode(target);
            RefreshLocalModeStatus(target);

            if (!target)
            {
                // Probe API to verify it's actually reachable
                IsBusy = true;
                try
                {
                    var reachable = await _localMode.ProbeApiAsync();
                    ShowStatus(reachable ? "API reachable — switched to API mode." : "API unreachable — staying in local mode.");
                }
                finally { IsBusy = false; }
            }
        }

        [RelayCommand]
        async Task ProbeApiAsync()
        {
            IsBusy = true;
            try
            {
                var ok = await _localMode.ProbeApiAsync();
                ApiStatusLabel = ok ? "✓ API reachable" : "✗ API unreachable";
                ShowStatus(ApiStatusLabel);
            }
            finally { IsBusy = false; }
        }

        // ── Local server CRUD ───────────────────────────────────────────────

        [RelayCommand]
        void AddServer()
        {
            IsNewEntry = true;
            IsEditing = true;
            ClearEditForm();
        }

        [RelayCommand]
        void EditServer(LocalServerEntry entry)
        {
            SelectedServer = entry;
            IsNewEntry = false;
            IsEditing = true;
            LoadEditForm(entry);
        }

        [RelayCommand]
        async Task SaveServerAsync()
        {
            if (string.IsNullOrWhiteSpace(EditHost))
            {
                ShowStatus("Host is required.");
                return;
            }

            int.TryParse(EditPort, out var port);

            var entry = IsNewEntry ? new LocalServerEntry() : (SelectedServer ?? new LocalServerEntry());
            entry.Name = string.IsNullOrEmpty(EditName) ? $"{EditHost}:{EditPort}" : EditName;
            entry.Host = EditHost.Trim();
            entry.Port = port > 0 ? port : 443;
            entry.Protocol = EditProtocol;
            entry.AuthToken = EditAuth.Trim();
            entry.ObfsType = string.IsNullOrEmpty(EditObfsType) ? null : EditObfsType.Trim();
            entry.ObfsPassword = string.IsNullOrEmpty(EditObfsPassword) ? null : EditObfsPassword;
            entry.PortsRange = string.IsNullOrEmpty(EditPortsRange) ? null : EditPortsRange.Trim();
            entry.LazyTls = EditLazyTls;
            entry.RawConfig = string.IsNullOrEmpty(EditRawConfig) ? null : EditRawConfig;

            if (IsNewEntry)
                await _localConfig.AddServerAsync(entry);
            else
                await _localConfig.UpdateServerAsync(entry);

            IsEditing = false;
            RefreshServerList();
            ShowStatus($"Server '{entry.Name}' saved.");
        }

        [RelayCommand]
        void CancelEdit()
        {
            IsEditing = false;
        }

        [RelayCommand]
        async Task DeleteServerAsync(LocalServerEntry entry)
        {
            await _localConfig.RemoveServerAsync(entry.Id);
            RefreshServerList();
            ShowStatus($"Server '{entry.Name}' deleted.");
        }

        [RelayCommand]
        async Task SetDefaultServerAsync(LocalServerEntry entry)
        {
            await _localConfig.SetDefaultServerAsync(entry.Id);
            RefreshServerList();
            ShowStatus($"'{entry.Name}' set as default.");
        }

        [RelayCommand]
        async Task ConnectLocalServerAsync(LocalServerEntry entry)
        {
            IsBusy = true;
            try
            {
                await _vpnManager.ConnectLocalAsync(entry.Id);
                ShowStatus($"Connecting to {entry.Name}...");
            }
            catch (Exception ex)
            {
                ShowStatus($"Failed: {ex.Message}");
                AppendLog($"[CONNECT-LOCAL] {ex.Message}");
            }
            finally { IsBusy = false; }
        }

        [RelayCommand]
        void GenerateConfigPreview()
        {
            var entry = new LocalServerEntry
            {
                Host = EditHost, Port = int.TryParse(EditPort, out var p) ? p : 443,
                AuthToken = EditAuth, ObfsType = EditObfsType, ObfsPassword = EditObfsPassword,
                PortsRange = EditPortsRange, LazyTls = EditLazyTls
            };
            var cfg = new Hysteria2Config
            {
                Server = $"{entry.Host}:{entry.Port}", Auth = entry.AuthToken,
                Obfs = entry.ObfsType, ObfsPassword = entry.ObfsPassword ?? string.Empty,
                PortsRange = entry.PortsRange, LazyTls = entry.LazyTls
            };
            EditRawConfig = cfg.ToConfig();
        }

        // ── Direct connect override ─────────────────────────────────────────

        [RelayCommand]
        async Task ConnectWithOverrideAsync()
        {
            if (string.IsNullOrWhiteSpace(ServerHost))
            {
                ShowStatus("Server host is required.");
                return;
            }

            IsBusy = true;
            try
            {
                var protocolType = SelectedProtocol switch
                {
                    "OlcRtc" => ProtocolType.OlcRtc,
                    _ => ProtocolType.Hysteria2
                };

                ProtocolConfig config = protocolType switch
                {
                    ProtocolType.Hysteria2 => new Hysteria2Config
                    {
                        ServerId = "admin-override",
                        Name = "Admin Override",
                        Server = $"{ServerHost}:{ServerPort}",
                        Auth = AuthToken,
                        Obfs = string.IsNullOrEmpty(ObfsType) ? null : ObfsType,
                        ObfsPassword = ObfsPassword,
                        PortsRange = PortsRange,
                        LazyTls = LazyTls,
                        RenderedConfig = string.IsNullOrEmpty(RawConfig) ? null : RawConfig,
                        Socks5Address = "127.0.0.1:1080"
                    },
                    ProtocolType.OlcRtc => new OlcRtcConfig
                    {
                        ServerId = "admin-override",
                        Name = "Admin Override",
                        SignalServer = $"{ServerHost}:{ServerPort}"
                    },
                    _ => throw new NotSupportedException()
                };

                var protocol = _protocolFactory.Create(protocolType);
                protocol.ErrorOccurred += (_, e) => AppendLog($"[ERROR] {e.Code}: {e.Message}");
                protocol.OutputReceived += (_, line) =>
                    MainThread.BeginInvokeOnMainThread(() => AppendLog(line));

                await protocol.ConnectAsync(config);
                ShowStatus("Connected with admin override config.");
            }
            catch (Exception ex)
            {
                AppendLog($"[EXCEPTION] {ex.GetType().Name}: {ex.Message}");
                ShowStatus($"Failed: {ex.Message}");
            }
            finally { IsBusy = false; }
        }

        [RelayCommand]
        async Task DisconnectAsync()
        {
            IsBusy = true;
            try { await _vpnManager.DisconnectAsync(); ShowStatus("Disconnected."); }
            catch (Exception ex) { ShowStatus($"Error: {ex.Message}"); }
            finally { IsBusy = false; }
        }

        [RelayCommand]
        void LoadRawConfigFromPreset()
        {
            if (SelectedProtocol == "Hysteria2")
            {
                var cfg = new Hysteria2Config
                {
                    Server = $"{ServerHost}:{ServerPort}",
                    Auth = AuthToken,
                    Obfs = string.IsNullOrEmpty(ObfsType) ? null : ObfsType,
                    ObfsPassword = ObfsPassword,
                    PortsRange = PortsRange,
                    LazyTls = LazyTls
                };
                RawConfig = cfg.ToConfig();
            }
        }

        // ── Logs ────────────────────────────────────────────────────────────

        [RelayCommand]
        void ClearLogs() => LogText = string.Empty;

        [RelayCommand]
        async Task ExportLogsAsync()
        {
            try
            {
                var path = _errorReporting.LastReportArchivePath;
                if (string.IsNullOrEmpty(path))
                {
                    await _errorReporting.FlushAsync();
                    path = _errorReporting.LastReportArchivePath;
                }
                if (!string.IsNullOrEmpty(path))
                    await Share.RequestAsync(new ShareFileRequest("Horus Logs", new ShareFile(path)));
            }
            catch (Exception ex) { ShowStatus($"Export failed: {ex.Message}"); }
        }

        // ── Private helpers ─────────────────────────────────────────────────

        private void RefreshLocalModeStatus(bool isLocal)
        {
            IsLocalMode = isLocal;
            LocalModeLabel = isLocal ? "LOCAL MODE — API bypassed" : "API MODE — connected to server";
        }

        private void RefreshServerList()
        {
            LocalServers = [.. _localConfig.Config.Servers];
        }

        private void ClearEditForm()
        {
            EditName = string.Empty; EditHost = string.Empty; EditPort = "443";
            EditProtocol = "Hysteria2"; EditAuth = string.Empty; EditObfsType = string.Empty;
            EditObfsPassword = string.Empty; EditPortsRange = string.Empty;
            EditLazyTls = false; EditRawConfig = string.Empty;
        }

        private void LoadEditForm(LocalServerEntry e)
        {
            EditName = e.Name; EditHost = e.Host; EditPort = e.Port.ToString();
            EditProtocol = e.Protocol; EditAuth = e.AuthToken;
            EditObfsType = e.ObfsType ?? string.Empty; EditObfsPassword = e.ObfsPassword ?? string.Empty;
            EditPortsRange = e.PortsRange ?? string.Empty; EditLazyTls = e.LazyTls;
            EditRawConfig = e.RawConfig ?? string.Empty;
        }

        private void AppendLog(string line)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                LogText += $"[{DateTime.Now:HH:mm:ss.fff}] {line}\n";
                var lines = LogText.Split('\n');
                if (lines.Length > 1000)
                    LogText = string.Join('\n', lines.TakeLast(800));
            });
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
    }
}
#endif
