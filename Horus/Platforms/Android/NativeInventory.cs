using Horus.Application.Diagnostics;
using Horus.Domain.Models;
using Horus.Protocols;
using System.Security.Cryptography;

namespace Horus.Platforms.Android
{
    /// <summary>
    /// Identifies the native libraries actually loaded on this device.
    ///
    /// <para>Answers a question that comes up more often than it should: "which build of
    /// the core is this?". Distribution is a direct APK, so devices run whatever they last
    /// installed, and a bug report against a core that was replaced two releases ago is
    /// worse than no report. A hash settles it without anyone having to remember.</para>
    ///
    /// <para>Also records segment alignment. Android 15+ devices may use 16 KB pages, and a
    /// library built with the older 4 KB alignment fails to load there — a failure that
    /// presents as "the app crashes on my new phone" with nothing else to go on. hev is
    /// already built correctly (see <c>Platforms/Android/lib/README.md</c>); libxray.so is
    /// worth checking, and this is where the answer will be when it matters.</para>
    ///
    /// <para>Hashing runs once, lazily, off the calling thread: the libraries are tens of
    /// megabytes and nothing here is needed until a report is collected.</para>
    /// </summary>
    internal static class NativeInventory
    {
        private static volatile string? _cached;

        public static void Register()
        {
            StateSnapshot.Register("native", 90, Describe);
            _ = Task.Run(() => _cached = Build());
        }

        private static IEnumerable<KeyValuePair<string, string?>> Describe()
        {
            yield return new("abi", string.Join(", ", global::Android.OS.Build.SupportedAbis ?? []));
            yield return new("coreVersion", XrayProtocol.CoreVersion);
            yield return new("coreRunning", XrayProtocol.IsCoreRunning.ToString());

            var text = _cached;
            if (text is null)
            {
                yield return new("libraries", "hashing…");
                yield break;
            }

            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var split = line.Split('=', 2);
                if (split.Length == 2) yield return new(split[0], split[1]);
            }
        }

        /// <summary>The contents of <c>native.txt</c> in the diagnostics archive.</summary>
        public static string Report() => _cached ?? Build();

        private static string Build()
        {
            var lines = new List<string>();

            try
            {
                var dir = global::Android.App.Application.Context.ApplicationInfo?.NativeLibraryDir;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    return "nativeLibraryDir=unavailable\n";

                lines.Add($"nativeLibraryDir={dir}");

                foreach (var path in Directory.GetFiles(dir, "*.so").OrderBy(x => x))
                {
                    var name = Path.GetFileName(path);
                    try
                    {
                        var info = new FileInfo(path);
                        using var stream = File.OpenRead(path);
                        var hash = Convert.ToHexString(SHA256.HashData(stream))[..16].ToLowerInvariant();
                        lines.Add($"{name}={hash}… {info.Length / 1024} KB");
                    }
                    catch (Exception ex)
                    {
                        lines.Add($"{name}=unreadable ({ex.GetType().Name})");
                    }
                }
            }
            catch (Exception ex)
            {
                lines.Add($"error={ex.Message}");
            }

            return string.Join('\n', lines) + "\n";
        }
    }
}
