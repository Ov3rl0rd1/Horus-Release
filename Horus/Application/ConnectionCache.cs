using Horus.Domain.Models;
using System.Text.Json;

namespace Horus.Application
{
    /// <summary>
    /// Keeps the last working set of connection endpoints on the device, so a reconnect
    /// does not have to ask the API first.
    ///
    /// <para><b>Why this is worth a cache at all.</b> The endpoints change rarely — a node's
    /// REALITY keys and Hysteria2 password are stable for as long as the account stays bound
    /// to it — but the round trip to fetch them sat in front of every single reconnect. On a
    /// mobile link that is hundreds of milliseconds to seconds, paid at exactly the moment
    /// the user has no working tunnel and the request has to go out over the bare network.
    /// Worse, it makes recovery depend on the API being reachable, which is not a
    /// prerequisite for the tunnel working.</para>
    ///
    /// <para><b>Invalidation is by failure, not by guesswork.</b> Nothing here can tell
    /// whether keys are still valid; only trying them can. So the cache is used first and
    /// discarded when every endpoint in it failed to connect — which is precisely the
    /// symptom of the two cases that matter: the account was moved to another node from a
    /// second device, or the node re-provisioned. The age cap is a backstop for the case
    /// where something changed without ever producing a failure.</para>
    ///
    /// <para>Stored in plain preferences. These are credentials, and on Android the app's
    /// preferences are already private to its UID — the same place the session token lives.
    /// 🔧 Worth revisiting together with the session store if that ever moves to
    /// EncryptedSharedPreferences.</para>
    /// </summary>
    public static class ConnectionCache
    {
        // Version suffix, bumped when the payload shape changes.
        //
        // /servers/connect stopped returning share links and now returns whole xray
        // outbounds. An entry written by an older build still deserialises — every field it
        // knew about is gone, so it yields a ServerConnection with an empty outbound list,
        // which reads as "the node offers nothing" and produces a connect failure rather
        // than a cache miss. Changing the key makes the old entry invisible instead.
        private const string PayloadKey = "horus.connect.cache.v2";
        private const string StampKey = "horus.connect.cache.v2.at";
        private const string ServerKey = "horus.connect.cache.v2.server";

        /// <summary>
        /// How old a cached set may be before it is re-fetched even though nothing failed.
        ///
        /// <para>A backstop, not the primary mechanism: a change that breaks connecting is
        /// caught by the failure path within one connect attempt, and this only covers a
        /// change that does not. A day is short enough that a silent server-side rotation
        /// is picked up by the next morning and long enough that ordinary use never pays for
        /// the request.</para>
        /// </summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

        /// <summary>
        /// The cached endpoints, or null when there are none, they are too old, or they
        /// belong to a different node than the one asked for.
        /// </summary>
        /// <param name="expectedServerId">
        /// The node the caller intends to use, or null for "whatever we are bound to".
        /// A mismatch discards the entry: endpoints are per node and using another node's
        /// keys fails in a way that looks like a broken tunnel.
        /// </param>
        public static ServerConnection? Read(int? expectedServerId = null)
        {
            try
            {
                var payload = Preferences.Get(PayloadKey, string.Empty);
                if (string.IsNullOrEmpty(payload)) return null;

                var stamp = Preferences.Get(StampKey, 0L);
                var age = DateTimeOffset.UtcNow - new DateTimeOffset(stamp, TimeSpan.Zero);
                if (stamp == 0 || age > MaxAge || age < TimeSpan.Zero)
                {
                    Diag.Info("cache", $"cached endpoints are {age.TotalHours:F0}h old — refetching");
                    return null;
                }

                if (expectedServerId is { } wanted)
                {
                    var cachedServer = Preferences.Get(ServerKey, 0);
                    if (cachedServer != 0 && cachedServer != wanted)
                    {
                        Diag.Info("cache", $"cached endpoints are for server {cachedServer}, want {wanted}");
                        return null;
                    }
                }

                var connection = JsonSerializer.Deserialize<ServerConnection>(payload, Json);
                if (connection is null || !connection.HasAny) return null;

                Diag.Info("cache", $"using cached endpoints ({age.TotalMinutes:F0} min old)");
                return connection;
            }
            catch (Exception ex)
            {
                Diag.Warn("cache", $"unreadable, ignoring: {ex.Message}");
                return null;
            }
        }

        /// <summary>Stores a set that has just been fetched and accepted.</summary>
        public static void Write(ServerConnection connection)
        {
            try
            {
                if (!connection.HasAny) return;

                Preferences.Set(PayloadKey, JsonSerializer.Serialize(connection, Json));
                Preferences.Set(StampKey, DateTimeOffset.UtcNow.UtcTicks);
                Preferences.Set(ServerKey, connection.Server?.Id ?? 0);
            }
            catch (Exception ex)
            {
                Diag.Warn("cache", $"could not store endpoints: {ex.Message}");
            }
        }

        /// <summary>
        /// Discards the cache. Called when every cached endpoint failed to connect, which
        /// is the only reliable signal that what is stored no longer works.
        /// </summary>
        public static void Invalidate(string reason)
        {
            try
            {
                Preferences.Remove(PayloadKey);
                Preferences.Remove(StampKey);
                Preferences.Remove(ServerKey);
                Diag.Info("cache", $"discarded: {reason}");
            }
            catch { }
        }

        internal static IEnumerable<KeyValuePair<string, string?>> Describe()
        {
            var stamp = SafeStamp();
            yield return new("cached", (stamp != 0).ToString());
            yield return new("cachedAgeMin", stamp == 0
                ? null
                : ((int)(DateTimeOffset.UtcNow - new DateTimeOffset(stamp, TimeSpan.Zero)).TotalMinutes).ToString());
            yield return new("cachedServerId", SafeServer()?.ToString());
        }

        private static long SafeStamp()
        {
            try { return Preferences.Get(StampKey, 0L); } catch { return 0; }
        }

        private static int? SafeServer()
        {
            try { var v = Preferences.Get(ServerKey, 0); return v == 0 ? null : v; } catch { return null; }
        }
    }
}
