using Android.Content;
using Android.Content.PM;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Platforms.Android
{
    /// <summary>
    /// Per-app split tunneling on Android.
    /// Uses VpnService.Builder.AddDisallowedApplication / AddAllowedApplication
    /// to specify which apps bypass or use the VPN tunnel.
    ///
    /// The selection is persisted and applied when the tunnel (re)starts.
    /// </summary>
    public class AndroidSplitTunnelingService : ISplitTunnelingService
    {
        private readonly Context _context;
        private readonly IVpnPlatformService _vpn;

        private SplitTunnelingMode _mode = SplitTunnelingMode.Disabled;
        private readonly List<string> _selected = [];

        public bool IsSupported => true;

        public SplitTunnelingMode Mode
        {
            get => _mode;
            set
            {
                _mode = value;
                HorusVpnTunnelService.SplitTunnelingMode = value;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public IReadOnlyList<string> SelectedEntries => _selected.AsReadOnly();

        public event EventHandler? SelectionChanged;

        public AndroidSplitTunnelingService(IVpnPlatformService vpn)
        {
            _vpn = vpn;
            _context = global::Android.App.Application.Context;
        }

        public async Task<IReadOnlyList<AppOrProcessEntry>> GetAvailableEntriesAsync()
        {
            return await Task.Run(() =>
            {
                var pm = _context.PackageManager;
                if (pm == null) return Array.Empty<AppOrProcessEntry>();

                var intent = new Intent(Intent.ActionMain);
                intent.AddCategory(Intent.CategoryLauncher);

                var apps = pm.QueryIntentActivities(intent, PackageInfoFlags.MetaData)
                    .Select(ri => new AppOrProcessEntry
                    {
                        Id = ri.ActivityInfo?.PackageName ?? string.Empty,
                        DisplayName = ri.LoadLabel(pm)?.ToString() ?? ri.ActivityInfo?.PackageName ?? string.Empty,
                        IsSystem = IsSystemApp(ri)
                    })
                    .Where(a => !string.IsNullOrEmpty(a.Id))
                    .OrderBy(a => a.IsSystem)
                    .ThenBy(a => a.DisplayName)
                    .ToList();

                return (IReadOnlyList<AppOrProcessEntry>)apps;
            });
        }

        public Task SetSelectedEntriesAsync(IEnumerable<string> entries)
        {
            _selected.Clear();
            _selected.AddRange(entries);

            // Propagate to tunnel service so next reconnect picks up the change
            HorusVpnTunnelService.SelectedApps = [.. _selected];

            SelectionChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task ApplyAsync()
        {
            // The tunnel will be reconfigured on next connect/reconnect.
            // If currently connected, trigger a reconnect to apply changes.
            HorusVpnTunnelService.SelectedApps = [.. _selected];
            return Task.CompletedTask;
        }

        private static bool IsSystemApp(ResolveInfo ri)
        {
            var flags = ri.ActivityInfo?.ApplicationInfo?.Flags ?? 0;
            return (flags & ApplicationInfoFlags.System) != 0;
        }
    }
}
