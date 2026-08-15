using System.Security.Cryptography;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application.Update
{
    /// <summary>
    /// Background updater: finds the newest release, fetches it when the conditions are
    /// right, verifies it, and hands it to the platform installer.
    ///
    /// <para><b>It runs in the app process on a plain timer, on purpose.</b> There is no
    /// job scheduler here. While the tunnel is up the foreground service keeps this process
    /// alive, which is the state the product is designed around — a user turns the VPN on
    /// and does not open the app again for weeks. When the VPN is off and the app is closed
    /// there is nothing to update from, and that is accepted: the next launch checks
    /// immediately. The upside is that on Android the timer is subject to Doze exactly like
    /// everything else, so "check every six hours" degrades into "check when the device
    /// happens to be awake", which is the behaviour we want anyway.</para>
    ///
    /// <para><b>Nothing here ever interrupts the user.</b> No prompts, no dialogs, no
    /// "restart to update" bar. The only thing shown is one quiet notification after the
    /// fact, on next launch. The policy that decides <i>when</i> lives in
    /// <see cref="UpdatePolicy"/> and is pure, so it can be tested; this class is the
    /// plumbing around it.</para>
    /// </summary>
    public sealed class UpdateService : IUpdateService, IDisposable
    {
        private const string KeyPendingVersion = "horus.update.pending.version";
        private const string KeyPendingSeen = "horus.update.pending.firstSeenUtc";
        private const string KeyPendingFile = "horus.update.pending.file";
        private const string KeyLastRunVersion = "horus.update.lastRunVersion";

        /// <summary>How often the sources are asked, independently of the condition polling.</summary>
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

        private readonly IUpdateSource[] _sources;
        private readonly IUpdateInstaller _installer;
        private readonly IDeviceConditions _conditions;
        private readonly IUserNotifier _notifier;
        private readonly IHttpClientFactory _http;
        private readonly VpnManager _vpn;
        private readonly IErrorReportingService _log;

        private CancellationTokenSource? _cts;
        private UpdatePlan? _plan;
        private string? _readyFile;
        private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;

        // Carried across restarts so the 24-hour deferral runs from when the update was
        // first seen. Without them a device that restarts daily would restart the clock
        // every time and a deferred update would never reach its deadline.
        private AppVersion _restoredVersion = AppVersion.Zero;
        private DateTimeOffset _restoredFirstSeen;

        public AppVersion CurrentVersion { get; }
        public AppVersion? JustUpdatedFrom { get; private set; }

        public UpdateService(
            IEnumerable<IUpdateSource> sources,
            IUpdateInstaller installer,
            IDeviceConditions conditions,
            IUserNotifier notifier,
            IHttpClientFactory http,
            VpnManager vpn,
            IErrorReportingService log)
        {
            // Order matters: GitHub first, the site as the fallback.
            _sources = [.. sources.OrderBy(s => s.Origin == UpdateOrigin.GitHub ? 0 : 1)];
            _installer = installer;
            _conditions = conditions;
            _notifier = notifier;
            _http = http;
            _vpn = vpn;
            _log = log;

            CurrentVersion = AppVersion.Parse(AppConfiguration.AppVersion);
            DetectCompletedUpdate();
        }

        // ── Lifecycle ───────────────────────────────────────────────────────

        public void Start()
        {
            if (!_installer.IsSupported || _installer.AssetSuffix is null)
            {
                Log("installer not supported on this build; updates disabled");
                return;
            }
            if (_cts is not null) return;

            RestorePlan();
            _cts = new CancellationTokenSource();
            _ = RunAsync(_cts.Token);
        }

        public void Stop()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        public void Dispose() => Stop();

        // ── The loop ────────────────────────────────────────────────────────

        private async Task RunAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try { await TickAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Log($"tick failed: {ex.Message}"); }

                    await Task.Delay(UpdatePolicy.NextPoll(_plan), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* stopped */ }
            catch (Exception ex) { Log($"loop died: {ex.Message}"); }
        }

        private async Task TickAsync(CancellationToken ct)
        {
            var now = DateTimeOffset.UtcNow;

            if (now - _lastCheck >= CheckInterval || _plan is null)
                await RefreshPlanAsync(now, ct).ConfigureAwait(false);

            if (_plan is not { } plan) return;

            var device = _conditions.Read();

            if (_readyFile is null)
            {
                var hold = UpdatePolicy.CanDownload(plan, device, now);
                if (hold != UpdateHold.None) { Log($"download held: {hold}"); return; }

                _readyFile = await DownloadAsync(plan, ct).ConfigureAwait(false);
                if (_readyFile is null) return;

                Preferences.Set(KeyPendingFile, _readyFile);
                Log($"{plan.Manifest.Version} downloaded and verified");
            }

            var connected = _vpn.State == VpnState.Connected;
            var installHold = UpdatePolicy.CanInstall(plan, device, connected, now, DateTime.Now.Hour);
            if (installHold != UpdateHold.None) { Log($"install held: {installHold}"); return; }

            await InstallAsync(plan, _readyFile, connected, ct).ConfigureAwait(false);
        }

        // ── Checking ────────────────────────────────────────────────────────

        public async Task<UpdateManifest?> CheckNowAsync(CancellationToken ct = default)
        {
            foreach (var source in _sources)
            {
                var manifest = await source.FetchLatestAsync(ct).ConfigureAwait(false);
                if (manifest is null) { Log($"{source.Origin}: no answer"); continue; }

                Log($"{source.Origin}: latest is {manifest.Version} ({manifest.Tag})");
                return manifest;
            }
            return null;
        }

        private async Task RefreshPlanAsync(DateTimeOffset now, CancellationToken ct)
        {
            _lastCheck = now;

            var manifest = await CheckNowAsync(ct).ConfigureAwait(false);
            if (manifest is null) return;

            var urgency = CurrentVersion.UrgencyOf(manifest.Version);
            if (urgency == UpdateUrgency.None)
            {
                if (_plan is not null) { Log("pending update is no longer newer; dropping"); ClearPlan(); }
                return;
            }

            var asset = manifest.Find(_installer.AssetSuffix!);
            if (asset is null)
            {
                Log($"{manifest.Version} publishes nothing matching {_installer.AssetSuffix}");
                return;
            }

            // A release with no digest cannot be verified. Refusing is the only safe
            // answer for a payload that will be executed with the user's privileges.
            if (asset.Sha256 is null)
            {
                Log($"{asset.Name} has no SHA-256 in the release; refusing to install it");
                return;
            }

            // Keep the original first-seen for the same version: the 24-hour deferral runs
            // from when the update appeared, not from the most recent poll or the most
            // recent app start, either of which would restart the clock forever.
            var firstSeen =
                _plan?.Manifest.Version == manifest.Version ? _plan.FirstSeenUtc
                : _restoredVersion == manifest.Version ? _restoredFirstSeen
                : now;

            if (_plan?.Manifest.Version != manifest.Version)
            {
                DiscardDownload();
                Log($"{CurrentVersion} -> {manifest.Version} ({urgency}, from {manifest.Origin})");
            }

            _plan = new UpdatePlan(manifest, asset, urgency, firstSeen);
            PersistPlan(_plan);
        }

        // ── Download + verify ───────────────────────────────────────────────

        private async Task<string?> DownloadAsync(UpdatePlan plan, CancellationToken ct)
        {
            var dir = Path.Combine(FileSystem.CacheDirectory, "updates");
            Directory.CreateDirectory(dir);

            // Named for the version so a stale payload from an abandoned update can never
            // be mistaken for this one.
            var target = Path.Combine(dir, $"{plan.Manifest.Version}-{plan.Asset.Name}");
            var partial = target + ".part";

            try
            {
                using var client = _http.CreateClient();
                client.Timeout = TimeSpan.FromMinutes(15); // ~60 MB on a bad mobile link

                using (var response = await client.GetAsync(
                        plan.Asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        Log($"download failed: HTTP {(int)response.StatusCode}");
                        return null;
                    }

                    await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var dst = File.Create(partial);
                    await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                }

                var digest = await ComputeSha256Async(partial, ct).ConfigureAwait(false);
                if (!string.Equals(digest, plan.Asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    // Truncated transfer, a cache serving the previous release, or
                    // something worse. Either way it must not be installed.
                    Log($"checksum mismatch for {plan.Asset.Name}: got {digest}, expected {plan.Asset.Sha256}");
                    TryDelete(partial);
                    return null;
                }

                TryDelete(target);
                File.Move(partial, target);
                return target;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log($"download failed: {ex.Message}");
                TryDelete(partial);
                return null;
            }
        }

        private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
        {
            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            return Sha256Sums.ToHex(hash);
        }

        // ── Install ─────────────────────────────────────────────────────────

        private async Task InstallAsync(UpdatePlan plan, string file, bool vpnConnected, CancellationToken ct)
        {
            // Bring the tunnel down ourselves rather than letting the installer yank the
            // process out from under it: on Windows that would leave the wintun adapter and
            // its routes behind, which is exactly the "no internet until reboot" failure
            // the crash-safety work went in to prevent.
            if (vpnConnected)
            {
                Log("stopping the tunnel before installing");
                try { await _vpn.DisconnectAsync().ConfigureAwait(false); }
                catch (Exception ex) { Log($"disconnect before install failed: {ex.Message}"); }
            }

            // Written before handing over, because on both platforms the installer may end
            // this process at any point after this call.
            Preferences.Set(KeyLastRunVersion, CurrentVersion.ToString());
            RememberReconnectIntent(vpnConnected);

            Log($"installing {plan.Manifest.Version} from {file}");
            try
            {
                await _installer.InstallAsync(file, plan.Manifest.Version, ct).ConfigureAwait(false);
                if (!_installer.TerminatesProcess) ClearPlan();
            }
            catch (Exception ex)
            {
                Log($"install failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Records that the tunnel was up, so the app can bring it back after the restart
        /// the install forces. Read by the Android package-replaced receiver and by
        /// startup on Windows.
        /// </summary>
        private static void RememberReconnectIntent(bool wasConnected) =>
            Preferences.Set(ReconnectAfterUpdateKey, wasConnected);

        public const string ReconnectAfterUpdateKey = "horus.update.reconnectAfterUpdate";

        // ── Post-update ─────────────────────────────────────────────────────

        /// <summary>
        /// Detects that this launch is the first after an update, by comparing the running
        /// version against the one recorded when the installer was invoked. This is the
        /// only user-visible part of the whole feature.
        /// </summary>
        private void DetectCompletedUpdate()
        {
            var previous = Preferences.Get(KeyLastRunVersion, string.Empty);
            if (string.IsNullOrEmpty(previous)) { Preferences.Set(KeyLastRunVersion, CurrentVersion.ToString()); return; }

            if (!AppVersion.TryParse(previous, out var before) || before >= CurrentVersion)
            {
                Preferences.Set(KeyLastRunVersion, CurrentVersion.ToString());
                return;
            }

            JustUpdatedFrom = before;
            Preferences.Set(KeyLastRunVersion, CurrentVersion.ToString());
            ClearPlan();

            _ = _notifier.NotifyAsync("Horus обновлён", $"Версия {CurrentVersion}");
        }

        // ── Persistence ─────────────────────────────────────────────────────

        private void PersistPlan(UpdatePlan plan)
        {
            Preferences.Set(KeyPendingVersion, plan.Manifest.Version.ToString());
            Preferences.Set(KeyPendingSeen, plan.FirstSeenUtc.ToUnixTimeSeconds().ToString());
        }

        /// <summary>
        /// Restores only what survives a restart: the version, when it was first seen and
        /// the verified payload. The manifest itself is re-fetched, since a URL from days
        /// ago is not worth trusting and the first tick asks anyway.
        /// </summary>
        private void RestorePlan()
        {
            var version = Preferences.Get(KeyPendingVersion, string.Empty);
            if (!AppVersion.TryParse(version, out var pending) || pending <= CurrentVersion)
            {
                ClearPlan();
                return;
            }

            _restoredVersion = pending;
            _restoredFirstSeen = long.TryParse(Preferences.Get(KeyPendingSeen, string.Empty), out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : DateTimeOffset.UtcNow;

            var file = Preferences.Get(KeyPendingFile, string.Empty);
            if (!string.IsNullOrEmpty(file) && File.Exists(file)) _readyFile = file;

            Log($"resuming pending update {pending}, first seen {_restoredFirstSeen:u}" +
                (_readyFile is null ? "" : " (already downloaded)"));
        }

        private void ClearPlan()
        {
            _plan = null;
            _restoredVersion = AppVersion.Zero;
            DiscardDownload();
            Preferences.Remove(KeyPendingVersion);
            Preferences.Remove(KeyPendingSeen);
        }

        private void DiscardDownload()
        {
            var file = Preferences.Get(KeyPendingFile, string.Empty);
            if (!string.IsNullOrEmpty(file)) TryDelete(file);
            Preferences.Remove(KeyPendingFile);
            _readyFile = null;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private void Log(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[Horus/update] {message}");
            try { _log.AppendLog($"[update] {message}"); } catch { }
        }
    }
}
