using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application.Notices
{
    /// <summary>
    /// Builds the Home screen list of actionable notices.
    ///
    /// <para><b>Nothing here polls.</b> Every input arrives as an event: the account
    /// changes, a permission re-read on resume finds something different, or the updater
    /// reports that it is parked. That matters more than it sounds — this runs on a phone
    /// whose whole selling point is a tunnel that survives the night, and a banner nobody
    /// can see is never worth a wakeup.</para>
    ///
    /// <para><b>At most two are shown.</b> Three system permissions and a subscription can
    /// all be wrong at once, and a Home screen that is four-fifths banner teaches the user
    /// to scroll past every one of them. The rest appear as the top ones are resolved.</para>
    /// </summary>
    public sealed class NoticeService : INoticeService
    {
        /// <summary>More than this and the screen stops being a VPN app.</summary>
        private const int MaxVisible = 2;

        /// <summary>
        /// How long a dismissal lasts. Not forever: every condition here comes back, and a
        /// notice that can be silenced permanently is one the user silences the day it
        /// first appears and never thinks about again.
        /// </summary>
        private static readonly TimeSpan DismissalPeriod = TimeSpan.FromDays(7);

        private readonly IAuthService _auth;
        private readonly ISystemPermissions _permissions;
        private readonly IUpdateService _updates;

        private IReadOnlyList<AppNotice> _current = [];

        public event EventHandler? Changed;

        public IReadOnlyList<AppNotice> Current => _current;

        public NoticeService(IAuthService auth, ISystemPermissions permissions, IUpdateService updates)
        {
            _auth = auth;
            _permissions = permissions;
            _updates = updates;

            _auth.AuthStateChanged += (_, __) => Refresh();
            _permissions.Changed += (_, __) => Refresh();
            _updates.BlockerChanged += (_, __) => Refresh();
        }

        public void Refresh()
        {
            var next = Build();

            // Only announce a real change: this runs on every resume and every auth event,
            // and re-raising an identical list would redraw Home for nothing.
            if (Same(_current, next)) return;

            _current = next;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private List<AppNotice> Build()
        {
            var notices = new List<AppNotice>();

            // 1. Subscription. First because it is the only one that stops the product
            //    working rather than degrading it.
            var daysLeft = DaysLeft();
            if (_auth.SubscriptionState != SubscriptionState.Unknown && daysLeft <= 7)
            {
                notices.Add(new AppNotice(
                    NoticeKind.Subscription,
                    daysLeft > 0 ? "Подписка заканчивается" : "Подписка не активна",
                    daysLeft > 0
                        ? $"Осталось {daysLeft} дн. — продлите, чтобы не потерять доступ"
                        : "Оформите подписку, чтобы включить защиту",
                    daysLeft > 0 ? "Продлить" : "Оформить",
                    NoticeTone.Suggestion,
                    CanDismiss: false));
            }

            // 2. Install permission. An update is downloaded and verified and cannot be
            //    applied — and until this was surfaced, all the user saw was the VPN
            //    switching itself off every couple of minutes.
            var updateParked = _updates.Blocker == UpdateBlocker.InstallPermission;
            if (!_permissions.CanInstallPackages && (updateParked || !Dismissed(NoticeKind.InstallPermission)))
            {
                notices.Add(new AppNotice(
                    NoticeKind.InstallPermission,
                    updateParked ? "Обновление ждёт разрешения" : "Обновления не установятся",
                    updateParked
                        ? "Новая версия скачана, но Android не даёт её установить"
                        : "Разрешите Horus устанавливать приложения, иначе обновления не придут",
                    "Разрешить",
                    NoticeTone.Problem,
                    // A parked update is blocking something real; dismissing it would hide
                    // the explanation for behaviour the user is already seeing.
                    CanDismiss: !updateParked));
            }

            // 3. Notifications. Without them the tunnel status is invisible and the install
            //    prompt cannot reach the user at all.
            if (!_permissions.NotificationsEnabled && !Dismissed(NoticeKind.Notifications))
            {
                notices.Add(new AppNotice(
                    NoticeKind.Notifications,
                    "Уведомления выключены",
                    "Без них не виден статус VPN и сообщения об обновлениях",
                    "Включить",
                    NoticeTone.Problem,
                    CanDismiss: true));
            }

            // 4. Battery optimisation. Last and dismissible on purpose: it is a
            //    recommendation, not a fault. It matters here more than in most apps — Doze
            //    suspending the app network is what ends tunnels overnight — but plenty of
            //    devices keep the tunnel alive without it, so presenting it as broken would
            //    be crying wolf.
            if (!_permissions.IgnoringBatteryOptimisations && !Dismissed(NoticeKind.BatteryOptimisation))
            {
                notices.Add(new AppNotice(
                    NoticeKind.BatteryOptimisation,
                    "VPN может отключаться во сне",
                    "Отключите оптимизацию батареи для Horus, чтобы туннель жил всю ночь",
                    "Настроить",
                    NoticeTone.Suggestion,
                    CanDismiss: true));
            }

            return notices.Count > MaxVisible ? notices.GetRange(0, MaxVisible) : notices;
        }

        private int DaysLeft()
        {
            var expiry = _auth.CurrentUser?.expiresAt;
            if (expiry is null) return 0;
            return Math.Max((int)Math.Ceiling((expiry.Value.ToUniversalTime() - DateTime.UtcNow).TotalDays), 0);
        }

        public async Task ActAsync(NoticeKind kind)
        {
            Diag.User("notice", $"acting on {kind}");

            // The payment sheet belongs to the view model; everything else is a system screen.
            if (kind == NoticeKind.Subscription) return;

            await _permissions.RequestAsync(kind).ConfigureAwait(false);

            // The outcome is not observable from here — the user is now outside the app.
            // The re-read happens when they come back, on resume.
        }

        public void Dismiss(NoticeKind kind)
        {
            if (!_current.Any(n => n.Kind == kind && n.CanDismiss)) return;

            Preferences.Set(DismissKey(kind), DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
            Diag.User("notice", $"dismissed {kind}");
            Refresh();
        }

        private static bool Dismissed(NoticeKind kind)
        {
            var raw = Preferences.Get(DismissKey(kind), string.Empty);
            if (!long.TryParse(raw, out var unix)) return false;

            return DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unix) < DismissalPeriod;
        }

        private static string DismissKey(NoticeKind kind) => $"horus.notice.dismissed.{kind}";

        private static bool Same(IReadOnlyList<AppNotice> a, IReadOnlyList<AppNotice> b)
        {
            if (a.Count != b.Count) return false;
            for (var i = 0; i < a.Count; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
