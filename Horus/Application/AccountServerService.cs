using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application
{
    /// <summary>
    /// Keeps <see cref="Current"/> on the account's server, moves it when the user picks
    /// another, and brings a running tunnel along when it moves.
    ///
    /// <para><b>How a move from elsewhere is noticed.</b> <c>GET /whoami</c> carries
    /// <c>currentServerId</c>, read past the API's session cache. Every refresh of the account
    /// is compared with what is known; while the tunnel runs the account is also asked on a
    /// timer of its own, because the account poll stops when the app is not on screen and a
    /// moved account leaves the tunnel on a node that no longer serves it.</para>
    /// </summary>
    public sealed class AccountServerService : IAccountServer
    {
        private static readonly TimeSpan WatchInterval = TimeSpan.FromMinutes(1);

        /// <summary>Slower with nobody looking: each ask is a request, and the tunnel recovers from a dead node on its own.</summary>
        private static readonly TimeSpan BackgroundWatchInterval = TimeSpan.FromMinutes(5);

        private readonly IApiService _api;
        private readonly IAccountSync _account;
        private readonly IVpnController _vpn;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private CancellationTokenSource? _watch;

        public AccountServerService(IApiService api, IAccountSync account, IVpnController vpn)
        {
            _api = api;
            _account = account;
            _vpn = vpn;

            // What the last connect used, until the account says otherwise.
            Current = ConnectionCache.Read()?.Server?.ToServerInfo();

            _account.AccountRefreshed += (_, _) => _ = ApplyAsync(_account.Last?.CurrentServerId);
            _vpn.StateChanged += OnVpnStateChanged;
            if (_vpn.State is VpnState.Connected) StartWatching();
        }

        public ServerInfo? Current { get; private set; }

        public event EventHandler? Changed;

        public async Task SelectAsync(ServerInfo server, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (!AccountServerRules.IsChange(server.Id, Current?.Id)) return;

                var bound = await _api.SelectServerAsync(server.Id, ct);
                Diag.User("servers", $"account moved to {bound.Name} ({bound.Location})");
                ConnectionCache.Invalidate("the account moved to another server");
                Set(bound.ToServerInfo());
            }
            finally
            {
                _gate.Release();
            }

            await FollowIfRunningAsync(ct);
        }

        /// <summary>Takes the account's server as <c>/whoami</c> reported it.</summary>
        private async Task ApplyAsync(int? accountServerId)
        {
            try
            {
                await _gate.WaitAsync();
                try
                {
                    if (!AccountServerRules.NeedsLookup(accountServerId, Current?.Id)) return;

                    if (accountServerId is not { } id)
                    {
                        Set(null);
                        return;
                    }

                    Diag.Info("servers", $"the account is on server {id}, the app knew {Current?.Id.ToString() ?? "none"}");
                    if (await DescribeAsync(id) is { } server) Set(server);
                }
                finally
                {
                    _gate.Release();
                }

                await FollowIfRunningAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Diag.Warn("servers", $"account server not applied: {ex.Message}");
            }
        }

        /// <summary>
        /// The account's server with its location. The stored endpoints name it when they are
        /// its own; otherwise <c>GET /servers/connect</c> does — it only reads the binding of an
        /// account that has one, and its answer is what the next connect needs anyway.
        /// </summary>
        private async Task<ServerInfo?> DescribeAsync(int serverId)
        {
            if (ConnectionCache.Read(serverId)?.Server is { } cached) return cached.ToServerInfo();

            ConnectionCache.Invalidate($"the account moved to server {serverId}");
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var connection = await _api.GetServerConnectionAsync(timeout.Token);
                ConnectionCache.Write(connection);
                return connection.Server?.ToServerInfo();
            }
            catch (Exception ex)
            {
                Diag.Warn("servers", $"server {serverId} not described: {ex.Message}");
                return null;
            }
        }

        private async Task FollowIfRunningAsync(CancellationToken ct)
        {
            if (!AccountServerRules.TunnelMustFollow(Current?.Id, _vpn.ActiveServer?.Id, _vpn.State)) return;

            Diag.Info("servers", $"tunnel on server {_vpn.ActiveServer?.Id}, account on {Current?.Id}: following");
            try { await _vpn.FollowAccountServerAsync(ct); }
            catch (Exception ex) { Diag.Warn("servers", $"the tunnel did not follow the account: {ex.Message}"); }
        }

        private void Set(ServerInfo? server)
        {
            Current = server;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // ── Watching while the tunnel runs ───────────────────────────────────

        private void OnVpnStateChanged(object? sender, VpnStateChangedEventArgs e)
        {
            switch (e.NewState)
            {
                case VpnState.Connected:
                    StartWatching();
                    // Also catches a connect that landed on a node the account has just left.
                    _ = _account.RefreshNowAsync();
                    break;
                case VpnState.Disconnected:
                    StopWatching();
                    break;
            }
        }

        private void StartWatching()
        {
            if (_watch is not null) return;
            var cts = new CancellationTokenSource();
            _watch = cts;
            _ = Task.Run(() => WatchAsync(cts.Token));
        }

        private void StopWatching()
        {
            _watch?.Cancel();
            _watch = null;
        }

        private async Task WatchAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(AppVisibility.IsForeground ? WatchInterval : BackgroundWatchInterval, ct);
                    await _account.RefreshNowAsync(ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Diag.Warn("servers", $"account server watch stopped: {ex.Message}"); }
        }
    }
}
