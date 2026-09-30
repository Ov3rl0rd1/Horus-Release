using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Horus.Domain.Models;

namespace Horus.Platforms.Windows
{
    /// <summary>
    /// Asks Windows how the previous session ended, when it ended without unwinding.
    ///
    /// <para>The session marker cannot tell a crash from the computer being shut down: both
    /// end the process without a clean exit. Windows keeps its own record of each.
    /// Application Error (1000), Application Hang (1002) and .NET Runtime (1023/1025/1026)
    /// name the process that died and, for a fault, the module it died in. User32 (1074)
    /// and Winlogon (7002) say the session was ended on purpose; Kernel-Power (41) and
    /// EventLog (6008) say the machine itself went down uncleanly. Reading them at the next
    /// start turns "ended without unwinding" into a verdict.</para>
    ///
    /// <para>Through <c>wevtutil</c> rather than <c>EventLogReader</c>, which is a separate
    /// package; this runs once, only after an abrupt end, off the UI thread.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class PreviousSessionInspector
    {
        private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

        /// <summary>
        /// How far past the last heartbeat a shutdown may be logged and still be the thing
        /// that ended the session: one heartbeat interval, plus a shutdown held up by apps.
        /// </summary>
        private static readonly TimeSpan EndingSlack = TimeSpan.FromMinutes(5);

        private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);

        /// <summary>Other programs' faults share these IDs; enough headroom to still reach ours.</summary>
        private const int MaxEvents = 200;

        private sealed record WinEvent(
            DateTimeOffset At, string Provider, int Id, IReadOnlyList<(string? Name, string Value)> Data)
        {
            /// <summary>A named field, falling back to its position for providers that do not name them.</summary>
            public string Get(string name, int position)
            {
                foreach (var (key, value) in Data)
                    if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;

                return position < Data.Count && Data[position].Name is null ? Data[position].Value : "?";
            }

            public string Joined => string.Join(" | ", Data.Select(d => d.Value).Where(v => v.Length > 0));
        }

        /// <param name="startedAt">When the previous session started, if known.</param>
        /// <param name="lastAlive">Its last heartbeat, if it ran one.</param>
        /// <param name="nativeOutputFound">Whether it left output on stderr, which already names a Go exit.</param>
        public static void InspectInBackground(DateTimeOffset? startedAt, DateTimeOffset? lastAlive, bool nativeOutputFound) =>
            _ = Task.Run(async () =>
            {
                try { await InspectAsync(startedAt, lastAlive, nativeOutputFound).ConfigureAwait(false); }
                catch (Exception ex) { Diag.Trace("app", $"previous session not inspected: {ex.Message}"); }
            });

        private static async Task InspectAsync(DateTimeOffset? startedAt, DateTimeOffset? lastAlive, bool nativeOutputFound)
        {
            // The heartbeat narrows the end to a minute. Without one — a session from a build
            // that did not write it — the whole session is in scope.
            var from = (lastAlive ?? startedAt ?? DateTimeOffset.Now.AddDays(-1)) - TimeSpan.FromMinutes(1);
            var since = $"TimeCreated[@SystemTime>='{from.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)}']";

            var crashes = (await QueryAsync("Application",
                    $"*[System[(EventID=1000 or EventID=1002 or EventID=1023 or EventID=1025 or EventID=1026) and {since}]]")
                .ConfigureAwait(false))
                .Where(e => e.Provider is "Application Error" or "Application Hang" or ".NET Runtime")
                .Where(e => e.Joined.Contains("Horus", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.At)
                .ToList();

            var endings = (await QueryAsync("System",
                    $"*[System[(EventID=41 or EventID=1074 or EventID=6008 or EventID=7002) and {since}]]")
                .ConfigureAwait(false))
                .Where(e => (e.Provider, e.Id) is
                    ("User32", 1074) or
                    ("Microsoft-Windows-Winlogon", 7002) or
                    ("Microsoft-Windows-Kernel-Power", 41) or
                    ("EventLog", 6008))
                .OrderBy(e => e.At)
                .ToList();

            var scope = lastAlive is { } alive
                ? $"last sign of life {alive:dd.MM HH:mm:ss}"
                : startedAt is { } started ? $"session started {started:dd.MM HH:mm}, no heartbeat" : "no marker times";

            var found = crashes.Concat(endings).OrderBy(e => e.At).Select(Line).ToList();
            var detail = $"Windows event log from {from:dd.MM HH:mm} ({scope})" +
                         (found.Count == 0 ? ": nothing relevant" : ":\n" + string.Join("\n", found));

            if (crashes.Count > 0)
            {
                Diag.Error("app", $"previous session: Windows recorded a crash — {Describe(crashes[0])}", detail);
                return;
            }

            // A deliberate end of the Windows session: the process could not have outlived it.
            // Only close to the last heartbeat does it explain the end; later, it merely
            // happened after a process that was already gone.
            var deadline = (lastAlive ?? DateTimeOffset.Now) + EndingSlack;
            var ending = endings.FirstOrDefault(e => e.Id is 1074 or 7002 && e.At <= deadline);
            if (ending is not null)
            {
                Diag.Info("app", lastAlive is null
                    ? $"previous session ended no later than Windows did — {Describe(ending)} at {ending.At:dd.MM HH:mm}"
                    : $"previous session ended with Windows — {Describe(ending)} at {ending.At:dd.MM HH:mm}; not a crash",
                    detail);
                return;
            }

            var unclean = endings.FirstOrDefault(e => e.Id is 41 or 6008);
            if (unclean is not null)
            {
                Diag.Warn("app",
                    $"previous session: the computer went down uncleanly (power loss, BSOD or reset) — {Describe(unclean)}",
                    detail);
                return;
            }

            if (nativeOutputFound)
            {
                Diag.Trace("app", "previous session: Windows recorded nothing further", detail);
                return;
            }

            Diag.Warn("app",
                "previous session: Windows recorded neither a crash nor a shutdown — a forced kill, " +
                "or a runtime exit that printed nothing",
                detail);
        }

        private static string Line(WinEvent e) => $"{e.At:dd.MM HH:mm:ss} {e.Provider} {e.Id}: {Describe(e)}";

        private static string Describe(WinEvent e) => (e.Provider, e.Id) switch
        {
            ("Application Error", 1000) =>
                $"fault in {e.Get("ModuleName", 3)} {e.Get("ModuleVersion", 4)}, " +
                $"exception {Hex(e.Get("ExceptionCode", 6))}, offset {e.Get("FaultingOffset", 7)}",
            ("Application Hang", 1002) => "stopped responding and was closed",
            (".NET Runtime", _) => DescribeDotNet(e),
            ("User32", 1074) => $"{e.Get("param5", 4)} requested by {e.Get("param1", 0)}",
            ("Microsoft-Windows-Winlogon", 7002) => "user signed out",
            ("Microsoft-Windows-Kernel-Power", 41) => "restarted without a clean shutdown",
            ("EventLog", 6008) => $"previous shutdown was unexpected ({e.Joined})",
            _ => e.Joined
        };

        /// <summary>The runtime's report is one block of text; its Description and exception lines say what happened.</summary>
        private static string DescribeDotNet(WinEvent e)
        {
            var lines = (e.Data.Count > 0 ? e.Data[0].Value : string.Empty)
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            var parts = lines
                .Where(l => l.StartsWith("Description:", StringComparison.Ordinal) ||
                            l.StartsWith("Exception Info:", StringComparison.Ordinal) ||
                            l.StartsWith("Message:", StringComparison.Ordinal))
                .Take(2)
                .ToList();

            var text = parts.Count > 0 ? string.Join(" ", parts) : lines.FirstOrDefault() ?? e.Joined;
            return text.Length <= 400 ? text : text[..400];
        }

        private static string Hex(string code) =>
            code.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || code == "?" ? code : "0x" + code;

        // ── wevtutil ─────────────────────────────────────────────────────────

        private static async Task<List<WinEvent>> QueryAsync(string log, string xpath)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wevtutil.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // /uni:true fixes the output to UTF-16 instead of the console code page, which on a
            // Russian system mangles every localised field.
            foreach (var arg in new[] { "qe", log, "/q:" + xpath, "/f:xml", "/rd:true", $"/c:{MaxEvents}", "/uni:true" })
                psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("wevtutil did not start");

            using var output = new MemoryStream();
            var copy = proc.StandardOutput.BaseStream.CopyToAsync(output);
            var errors = proc.StandardError.ReadToEndAsync();

            using (var timeout = new CancellationTokenSource(QueryTimeout))
            {
                try
                {
                    await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(); } catch { }
                    throw new TimeoutException($"wevtutil {log} did not answer in {QueryTimeout.TotalSeconds:0} s");
                }
            }

            await copy.ConfigureAwait(false);

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"wevtutil {log} → {proc.ExitCode}: {(await errors.ConfigureAwait(false)).Trim()}");

            return Parse(Decode(output.ToArray()));
        }

        /// <summary>
        /// UTF-16 as asked for, but tolerant of a build that ignores <c>/uni</c>: ASCII is all
        /// the verdict needs, and it survives either reading.
        /// </summary>
        private static string Decode(byte[] bytes)
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

            if (bytes.Length >= 2 && bytes[0] != 0 && bytes[1] == 0)
                return Encoding.Unicode.GetString(bytes);

            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>wevtutil prints one <c>Event</c> element after another with no root, so this reads a fragment.</summary>
        private static List<WinEvent> Parse(string xml)
        {
            var result = new List<WinEvent>();
            if (string.IsNullOrWhiteSpace(xml)) return result;

            var settings = new XmlReaderSettings
            {
                ConformanceLevel = ConformanceLevel.Fragment,
                CheckCharacters = false,
                DtdProcessing = DtdProcessing.Prohibit
            };

            using var reader = XmlReader.Create(new StringReader(xml), settings);
            reader.MoveToContent();

            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Event")
                {
                    if (XNode.ReadFrom(reader) is XElement element && Read(element) is { } e)
                        result.Add(e);
                }
                else
                {
                    reader.Read();
                }
            }

            return result;
        }

        private static WinEvent? Read(XElement element)
        {
            var system = element.Element(Ns + "System");
            var provider = system?.Element(Ns + "Provider")?.Attribute("Name")?.Value;
            var idText = system?.Element(Ns + "EventID")?.Value;
            var time = system?.Element(Ns + "TimeCreated")?.Attribute("SystemTime")?.Value;

            if (provider is null ||
                !int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ||
                time is null ||
                !DateTimeOffset.TryParse(TrimFraction(time), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var at))
                return null;

            var data = element.Element(Ns + "EventData")?.Elements(Ns + "Data")
                .Select(d => (d.Attribute("Name")?.Value, d.Value.Trim()))
                .ToList() ?? [];

            // Some providers carry their payload under UserData instead; flatten it.
            if (data.Count == 0 && element.Element(Ns + "UserData") is { } user)
                data = user.Descendants()
                    .Where(d => !d.HasElements)
                    .Select(d => ((string?)d.Name.LocalName, d.Value.Trim()))
                    .ToList();

            return new WinEvent(at.ToLocalTime(), provider, id, data);
        }

        /// <summary>Event times can carry more fractional digits than DateTimeOffset parses.</summary>
        private static string TrimFraction(string time) => Regex.Replace(time, @"(\.\d{7})\d+", "$1");
    }
}
