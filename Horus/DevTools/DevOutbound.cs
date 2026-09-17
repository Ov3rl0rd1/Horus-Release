using Horus.Application;
using Horus.Domain.Models;
using Horus.Presentation.Navigation;

namespace Horus.DevTools
{
    /// <summary>
    /// ⚠ <b>DEV-PANEL — pre-release test harness. Not a product feature.</b>
    ///
    /// <para>Exists so a new protocol can be exercised on its own: pin one of the node's
    /// outbounds, and the connect path stops falling back to the others. Without it a
    /// protocol that fails is silently replaced by one that works, which is exactly the
    /// behaviour that makes "does hysteria actually come up on this network?" unanswerable
    /// from the UI.</para>
    ///
    /// <para><b>To delete the whole thing:</b> remove this folder and the four
    /// <c>DEV-PANEL</c> blocks that reference it —</para>
    /// <list type="bullet">
    /// <item><c>Application/VpnManager.cs</c> — one line in <c>ConnectAsync</c></item>
    /// <item><c>Presentation/ViewModels/MainViewModel.cs</c> — one region</item>
    /// <item><c>Presentation/View/Screens/HomeView.xaml</c> — a gesture and a label</item>
    /// <item><c>Presentation/View/Screens/HomeViewDesktop.xaml</c> — the same two, for the
    /// desktop layout Windows actually renders</item>
    /// </list>
    /// <para><c>grep -rn "DEV-PANEL"</c> finds every one of them. Nothing else in the app
    /// knows this type exists, and no existing behaviour changes while nothing is pinned.</para>
    /// </summary>
    public static class DevOutbound
    {
        private const string Key = "horus.dev.forcedOffer";

        /// <summary>
        /// The offer id the next connect is restricted to, or null for normal behaviour.
        ///
        /// <para>Persisted, because the point is to leave a build pinned to one protocol
        /// across restarts and see whether it survives a night. That also makes it a foot-gun:
        /// a tester who forgets is testing one protocol while believing they are testing the
        /// app, which is what <see cref="Badge"/> is for.</para>
        /// </summary>
        public static string? ForcedOfferId
        {
            get
            {
                try
                {
                    var value = Preferences.Get(Key, string.Empty);
                    return string.IsNullOrWhiteSpace(value) ? null : value;
                }
                catch { return null; }
            }
            private set
            {
                try
                {
                    if (value is null) Preferences.Remove(Key);
                    else Preferences.Set(Key, value);
                }
                catch { }
            }
        }

        /// <summary>Caption under the status line. Empty — and invisible — when nothing is pinned.</summary>
        public static string Badge =>
            ForcedOfferId is { } id ? $"⚙ только «{id}», без фолбэка" : string.Empty;

        /// <summary>
        /// Narrows the candidate list to the pinned offer.
        ///
        /// <para><b>Throws rather than falling back</b> when the pinned offer is not among
        /// the ones this node published. Quietly connecting with something else would be the
        /// one outcome this whole file exists to prevent, and a tester who moved to a node
        /// without that protocol needs to be told, not accommodated.</para>
        /// </summary>
        public static List<ConnectionCandidate> Restrict(List<ConnectionCandidate> available)
        {
            if (ForcedOfferId is not { } wanted) return available;

            var pinned = available.FirstOrDefault(c => c.Id == wanted);

            if (pinned is null)
                throw new InvalidOperationException(
                    $"[DEV] Оутбаунд «{wanted}» закреплён, но узел его не предлагает. " +
                    $"Доступны: {string.Join(", ", available.Select(c => c.Id))}. " +
                    "Снимите закрепление в тест-панели.");

            Diag.Info("dev", $"forced outbound {pinned.Id} ({pinned.ProtocolName}); fallback disabled");
            return [pinned];
        }

        /// <summary>
        /// The panel itself: pick an outbound, go back to automatic, or drop the cached
        /// profile so the next connect refetches it from the API.
        ///
        /// <para>The list comes from <see cref="ConnectionCache"/> rather than from the
        /// manager, so this needs no state of its own and no hook in the fetch path. The
        /// cost is that nothing is listed until the app has connected once — acceptable for
        /// a test build, and the panel says so.</para>
        /// </summary>
        public static async Task ShowAsync(VpnManager vpn)
        {
            var cached = ConnectionCache.Read(vpn.ActiveServer?.Id);
            IReadOnlyList<ConnectionCandidate> candidates = cached?.Candidates() ?? [];

            const string Auto = "Авто (все, с фолбэком)";
            const string Reset = "Сбросить кеш профиля";

            var buttons = new List<string>();

            foreach (var c in candidates)
            {
                var mark = c.Id == ForcedOfferId ? "✓ " : string.Empty;
                buttons.Add($"{mark}{c.Id} · {c.ProtocolName}");
            }

            if (ForcedOfferId is not null) buttons.Add(Auto);

            // Reset is passed as the destruction button rather than as an entry — MAUI
            // renders it separately, so listing it here too would show it twice.
            var title = candidates.Count > 0
                ? $"Тест-панель · {candidates.Count} оутбаунд(ов)"
                : "Тест-панель · список пуст, подключитесь один раз";

            var choice = await Dialog.ActionSheet(title, "Отмена", Reset, [.. buttons]);

            if (string.IsNullOrEmpty(choice) || choice == "Отмена") return;

            if (choice == Reset)
            {
                ConnectionCache.Invalidate("dev panel");

                // Reconnecting is the only thing that actually refetches — the cache is read
                // at the start of a connect, so dropping it while connected changes nothing
                // until the next one.
                if (vpn.State != VpnState.Disconnected)
                {
                    await Dialog.Alert("Кеш сброшен", "Переподключаюсь, чтобы забрать конфигурацию заново.");
                    await ReconnectAsync(vpn);
                }
                else
                {
                    await Dialog.Alert("Кеш сброшен", "Следующее подключение запросит конфигурацию с сервера.");
                }

                return;
            }

            if (choice == Auto)
            {
                ForcedOfferId = null;
                Diag.Info("dev", "forced outbound cleared");
                await Dialog.Alert("Тест-панель", "Закрепление снято — снова все оутбаунды с фолбэком.");
                await ReconnectAsync(vpn);
                return;
            }

            // "✓ id · protocol" → id. Split on the separator rather than matching the label,
            // because the tick makes the string differ from what was listed.
            var picked = choice.TrimStart('✓', ' ').Split(" · ")[0];

            var match = candidates.FirstOrDefault(c => c.Id == picked);
            if (match is null) return;

            ForcedOfferId = match.Id;
            Diag.Info("dev", $"pinned outbound {match.Id}");

            await ReconnectAsync(vpn);
        }

        /// <summary>
        /// Brings the tunnel down and back up so the choice takes effect now rather than at
        /// the next manual connect. Failures are shown rather than swallowed — this is a
        /// diagnostic tool, and a protocol that cannot come up is the result being looked for.
        /// </summary>
        private static async Task ReconnectAsync(VpnManager vpn)
        {
            try
            {
                if (vpn.State != VpnState.Disconnected) await vpn.DisconnectAsync();
                await vpn.ConnectAsync();
            }
            catch (Exception ex)
            {
                await Dialog.Alert("Не удалось подключиться", ex.Message);
            }
        }
    }
}
