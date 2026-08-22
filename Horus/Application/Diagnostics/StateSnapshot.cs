using System.Text;
using System.Text.Json;

namespace Horus.Application.Diagnostics
{
    /// <summary>
    /// A readable dump of what the app believed at the moment a report was collected.
    ///
    /// <para>This replaces the questions. Most support exchanges about a VPN are spent
    /// establishing facts the app already knows — is the core running, which protocol won,
    /// what did preflight say, is battery optimisation off, is Always-on enabled. Every one
    /// of those is one line here, and the user pastes a screenshot instead of answering ten
    /// messages.</para>
    ///
    /// <para><b>Sections register themselves.</b> A snapshot needs facts from
    /// <c>VpnManager</c>, the network monitor and the platform layer, and every one of them
    /// already depends on the reporting service — reaching back the other way would be a
    /// cycle. So contributors push a callback in and this class never references them.
    /// Rethink solves the same problem with a pile of <c>*Stats()</c> methods on the VPN
    /// service; a registry keeps the same result without the coupling.</para>
    ///
    /// <para>Callbacks run on whatever thread is building the report, must not block, and
    /// must not throw — a failing section is rendered as an error line so the rest of the
    /// snapshot survives it.</para>
    /// </summary>
    public static class StateSnapshot
    {
        private static readonly object Sync = new();
        private static readonly List<Section> Sections = [];

        private sealed record Section(int Order, string Name, Func<IEnumerable<KeyValuePair<string, string?>>> Read);

        /// <summary>
        /// Adds or replaces a section. Re-registering the same name overwrites, so a
        /// singleton rebuilt after a service restart does not produce duplicates.
        /// </summary>
        /// <param name="order">Sort key. Lower first; use 0–99 for the things read first.</param>
        public static void Register(string name, int order, Func<IEnumerable<KeyValuePair<string, string?>>> read)
        {
            lock (Sync)
            {
                Sections.RemoveAll(s => s.Name == name);
                Sections.Add(new Section(order, name, read));
            }
        }

        public static void Unregister(string name)
        {
            lock (Sync) Sections.RemoveAll(s => s.Name == name);
        }

        /// <summary>Every section, evaluated now. Failing sections yield an <c>!error</c> key.</summary>
        private static List<(string Name, Dictionary<string, string?> Values)> Collect()
        {
            List<Section> snapshot;
            lock (Sync) snapshot = [.. Sections.OrderBy(s => s.Order).ThenBy(s => s.Name)];

            var result = new List<(string, Dictionary<string, string?>)>(snapshot.Count);

            foreach (var section in snapshot)
            {
                try
                {
                    var values = new Dictionary<string, string?>();
                    foreach (var kv in section.Read()) values[kv.Key] = kv.Value;
                    result.Add((section.Name, values));
                }
                catch (Exception ex)
                {
                    result.Add((section.Name, new Dictionary<string, string?>
                    {
                        ["!error"] = $"{ex.GetType().Name}: {ex.Message}"
                    }));
                }
            }

            return result;
        }

        /// <summary>For the archive. Nested objects, one per section.</summary>
        public static string BuildJson()
        {
            var doc = new Dictionary<string, object>
            {
                ["collectedAt"] = DateTimeOffset.Now.ToString("O")
            };

            foreach (var (name, values) in Collect()) doc[name] = values;

            try
            {
                return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (Exception ex)
            {
                return $"{{ \"error\": \"{ex.Message}\" }}";
            }
        }

        /// <summary>
        /// For the Settings screen and for pasting into a chat. Plain text on purpose —
        /// the whole point is that it survives being screenshotted.
        /// </summary>
        public static string BuildText()
        {
            var sb = new StringBuilder();
            sb.Append("Horus — состояние на ").Append(DateTimeOffset.Now.ToString("dd.MM HH:mm:ss")).Append('\n');

            foreach (var (name, values) in Collect())
            {
                sb.Append('\n').Append(name).Append('\n');
                foreach (var (key, value) in values)
                    sb.Append("  ").Append(key).Append(": ").Append(value ?? "—").Append('\n');
            }

            return sb.ToString();
        }
    }
}
