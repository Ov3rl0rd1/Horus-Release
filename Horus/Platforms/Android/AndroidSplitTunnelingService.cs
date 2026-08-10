using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using System.Text;
using System.Text.Json;

// Android.Graphics also defines Path; every use here is the filesystem one.
using Path = System.IO.Path;

namespace Horus.Platforms.Android
{
    /// <summary>
    /// Per-app split tunneling on Android, via
    /// <c>VpnService.Builder.AddDisallowedApplication</c> / <c>AddAllowedApplication</c>.
    ///
    /// Enumerating installed apps is deceptively expensive: the package list itself is
    /// cheap, but every <c>LoadLabel</c> opens another package's resources, and every icon
    /// decodes a drawable. Both are cached on disk so only a first run — or an install /
    /// uninstall — pays the cost.
    /// </summary>
    public class AndroidSplitTunnelingService : ISplitTunnelingService
    {
        private const string ModeKey = "split_tunneling_mode";
        private const string SelectedKey = "split_tunneling_apps";

        /// <summary>Icons are rendered at this pixel size — list rows are 34dp.</summary>
        private const int IconPixels = 96;

        private readonly Context _context;
        private readonly IVpnPlatformService _vpn;

        private SplitTunnelingMode _mode = SplitTunnelingMode.Disabled;
        private readonly List<string> _selected = [];

        public bool IsSupported => true;

        public IReadOnlyList<string> AlwaysDirectEntries => AppConfiguration.BlockedPackages;

        public SplitTunnelingMode Mode
        {
            get => _mode;
            set
            {
                _mode = value;
                HorusVpnTunnelService.SplitTunnelingMode = value;
                Preferences.Set(ModeKey, (int)value);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public IReadOnlyList<string> SelectedEntries => _selected.AsReadOnly();

        public event EventHandler? SelectionChanged;

        public AndroidSplitTunnelingService(IVpnPlatformService vpn)
        {
            _vpn = vpn;
            _context = global::Android.App.Application.Context;

            // Restore the user's choice. Held only in memory before, so every restart
            // silently reverted split tunnelling to "все через VPN" while Settings still
            // implied the old selection was in force.
            _mode = (SplitTunnelingMode)Preferences.Get(ModeKey, (int)SplitTunnelingMode.Disabled);

            var stored = Preferences.Get(SelectedKey, string.Empty);
            if (!string.IsNullOrEmpty(stored))
                _selected.AddRange(stored.Split('\n', StringSplitOptions.RemoveEmptyEntries));

            PushToTunnelService();
        }

        // ── Enumeration ──────────────────────────────────────────────────────

        public async Task<IReadOnlyList<AppOrProcessEntry>> GetAvailableEntriesAsync()
        {
            return await Task.Run(() =>
            {
                var pm = _context.PackageManager;
                if (pm == null) return (IReadOnlyList<AppOrProcessEntry>)Array.Empty<AppOrProcessEntry>();

                var intent = new Intent(Intent.ActionMain);
                intent.AddCategory(Intent.CategoryLauncher);

                // No metadata flag: we only need the package name and the ApplicationInfo
                // flags, and asking for metadata makes the query measurably slower.
                var resolved = pm.QueryIntentActivities(intent, (PackageInfoFlags)0);

                var packages = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var ri in resolved)
                {
                    var pkg = ri.ActivityInfo?.PackageName;
                    if (string.IsNullOrEmpty(pkg) || pkg == _context.PackageName) continue;
                    packages[pkg] = IsSystemApp(ri);
                }

                var labels = LoadLabels(pm, packages.Keys);

                var apps = packages
                    .Select(kv => new AppOrProcessEntry
                    {
                        Id = kv.Key,
                        DisplayName = labels.TryGetValue(kv.Key, out var l) && l.Length > 0 ? l : kv.Key,
                        IsSystem = kv.Value,
                        IconPath = CachedIconPath(kv.Key) is var p && File.Exists(p) ? p : null
                    })
                    .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                return (IReadOnlyList<AppOrProcessEntry>)apps;
            });
        }

        /// <summary>
        /// Labels come from a JSON cache keyed by the exact set of installed packages, so a
        /// cold start pays for <c>LoadLabel</c> once and every later visit is a file read.
        /// The signature changes whenever an app is installed or removed, which is the only
        /// time a label can appear or disappear.
        /// </summary>
        private Dictionary<string, string> LoadLabels(PackageManager pm, IEnumerable<string> packages)
        {
            var ordered = packages.OrderBy(p => p, StringComparer.Ordinal).ToList();
            var signature = Signature(ordered);
            var cacheFile = Path.Combine(CacheDirectory, "app-labels.json");

            try
            {
                if (File.Exists(cacheFile))
                {
                    var cached = JsonSerializer.Deserialize<LabelCache>(File.ReadAllText(cacheFile));
                    if (cached?.Signature == signature && cached.Labels is { Count: > 0 })
                        return cached.Labels;
                }
            }
            catch { /* a corrupt cache just means we rebuild it */ }

            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pkg in ordered)
            {
                try
                {
                    var info = pm.GetApplicationInfo(pkg, (PackageInfoFlags)0);
                    labels[pkg] = pm.GetApplicationLabel(info) ?? pkg;
                }
                catch { labels[pkg] = pkg; }
            }

            try
            {
                File.WriteAllText(cacheFile,
                    JsonSerializer.Serialize(new LabelCache { Signature = signature, Labels = labels }));
            }
            catch { /* cache is an optimisation, not a requirement */ }

            return labels;
        }

        public async Task LoadIconsAsync(
            IReadOnlyList<AppOrProcessEntry> entries,
            Action<AppOrProcessEntry> onReady,
            CancellationToken ct = default)
        {
            await Task.Run(() =>
            {
                var pm = _context.PackageManager;
                if (pm == null) return;

                foreach (var entry in entries)
                {
                    if (ct.IsCancellationRequested) return;
                    if (entry.IconPath != null) continue;

                    var path = CachedIconPath(entry.Id);
                    if (!File.Exists(path) && !TryRenderIcon(pm, entry.Id, path)) continue;

                    entry.IconPath = path;
                    onReady(entry);
                }
            }, ct);
        }

        /// <summary>Rasterises an app icon once into the cache. Returns false if the package
        /// has no usable icon, which is normal for a few system entries.</summary>
        private static bool TryRenderIcon(PackageManager pm, string package, string path)
        {
            try
            {
                var drawable = pm.GetApplicationIcon(package);
                if (drawable == null) return false;

                using var bitmap = Bitmap.CreateBitmap(IconPixels, IconPixels, Bitmap.Config.Argb8888!);
                if (bitmap == null) return false;

                using (var canvas = new Canvas(bitmap))
                {
                    drawable.SetBounds(0, 0, IconPixels, IconPixels);
                    drawable.Draw(canvas);
                }

                // Write via a temp file: a half-written PNG left by a killed process would
                // otherwise be cached forever and render as a broken image.
                var temp = path + ".tmp";
                using (var stream = File.Create(temp))
                    bitmap.Compress(Bitmap.CompressFormat.Png!, 100, stream);

                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string CacheDirectory
        {
            get
            {
                var dir = Path.Combine(FileSystem.CacheDirectory, "appicons");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string CachedIconPath(string package) =>
            Path.Combine(CacheDirectory, package + ".png");

        private static string Signature(IEnumerable<string> packages)
        {
            var joined = string.Join('\n', packages);
            var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(joined));
            return Convert.ToHexString(hash);
        }

        // ── Selection ────────────────────────────────────────────────────────

        public Task SetSelectedEntriesAsync(IEnumerable<string> entries)
        {
            _selected.Clear();
            // Config-forced entries are implicit; keeping them out of the user's set stops
            // them being persisted as if the user had chosen them.
            _selected.AddRange(entries.Where(e => !AlwaysDirectEntries.Contains(e)));

            PushToTunnelService();
            Preferences.Set(SelectedKey, string.Join('\n', _selected));

            SelectionChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task ApplyAsync()
        {
            // The tunnel picks this up on the next connect/reconnect.
            PushToTunnelService();
            return Task.CompletedTask;
        }

        private void PushToTunnelService()
        {
            HorusVpnTunnelService.SplitTunnelingMode = _mode;
            HorusVpnTunnelService.SelectedApps = [.. _selected];
            HorusVpnTunnelService.AlwaysDirectApps = [.. AlwaysDirectEntries];
        }

        private static bool IsSystemApp(ResolveInfo ri)
        {
            var flags = ri.ActivityInfo?.ApplicationInfo?.Flags ?? 0;
            return (flags & ApplicationInfoFlags.System) != 0;
        }

        private sealed class LabelCache
        {
            public string Signature { get; set; } = string.Empty;
            public Dictionary<string, string> Labels { get; set; } = [];
        }
    }
}
