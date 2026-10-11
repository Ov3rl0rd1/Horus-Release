using System.Runtime.Versioning;
using Horus.Application;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using Horus.Platforms.Windows.Tunnel;
using Horus.Presentation.Navigation;
using Horus.Presentation.View.Controls;

namespace Horus.Platforms.Windows.Views
{
    /// <summary>
    /// The Windows part of Settings: connection options that actually do something here, and
    /// the gaming options. Takes the place of the shared "Соединение" card, whose switches on
    /// Windows were a kill switch that saved nothing, start-on-boot (an Android receiver) and
    /// "metered" (an Android notion).
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class WindowsSettingsSection : ContentView
    {
        private readonly IVpnController _vpn;
        private readonly Navigator _nav;
        private readonly WindowsSplitTunnelingService _split;
        private readonly Label _splitValue = Ui.Dim(null, 13);
        private readonly Border _reconnect;
        private WindowsTunSettings _appliedTun;
        private bool _appliedUdp;

        public WindowsSettingsSection(IVpnController vpn, Navigator nav, ISplitTunnelingService split)
        {
            _vpn = vpn;
            _nav = nav;
            _split = (WindowsSplitTunnelingService)split;
            _appliedTun = WindowsPreferences.TunSettings;
            _appliedUdp = WindowsPreferences.PreferUdpTransport;

            _reconnect = BuildReconnectRow();

            Content = new VerticalStackLayout
            {
                Spacing = 16,
                Children =
                {
                    Section("Соединение",
                        ToggleRow("Kill Switch",
                            "Если VPN не восстановился, интернет остаётся заблокированным, а не идёт напрямую",
                            WindowsPreferences.KillSwitch, v => WindowsPreferences.KillSwitch = v),
                        ToggleRow("Автоподключение",
                            "Подключаться при запуске Horus",
                            UserPreferences.AutoConnectOnLaunch, v => UserPreferences.AutoConnectOnLaunch = v),
                        LinkRow("Приложения", "Какие программы идут через VPN, и куда идёт их трафик сейчас", _splitValue,
                            () => _nav.Go(AppScreen.Apps))),
                    Section("Игры и скорость",
                        ToggleRow("Приоритет UDP-протокола",
                            "Сначала Hysteria2, если сервер его поддерживает: игровой UDP не ждёт потерянные TCP-пакеты",
                            WindowsPreferences.PreferUdpTransport, v => { WindowsPreferences.PreferUdpTransport = v; Changed(); }),
                        ToggleRow("Режим скорости",
                            "Крупные пакеты (MTU 9000): закачки быстрее, но во время закачки пинг в игре выше",
                            WindowsPreferences.SpeedMode, v => { WindowsPreferences.SpeedMode = v; Changed(); }),
                        ToggleRow("IPv6 через VPN",
                            "Выключайте только если игра или сеть не работает с IPv6 — тогда IPv6 пойдёт мимо VPN",
                            WindowsPreferences.CaptureIpv6, v => { WindowsPreferences.CaptureIpv6 = v; Changed(); })),
                    _reconnect,
                }
            };

            _split.SelectionChanged += (_, _) => MainThread.BeginInvokeOnMainThread(ShowSplit);
            _vpn.StateChanged += (_, e) => MainThread.BeginInvokeOnMainThread(() =>
            {
                if (e.NewState == VpnState.Connected && e.OldState != VpnState.Connected)
                {
                    // A fresh connection runs whatever is set now.
                    _appliedTun = WindowsPreferences.TunSettings;
                    _appliedUdp = WindowsPreferences.PreferUdpTransport;
                }
                Changed();
            });
            ShowSplit();
            Changed();
        }

        private void ShowSplit()
        {
            var count = _split.SelectedEntries.Count;
            _splitValue.Text = _split.Mode switch
            {
                SplitTunnelingMode.Blacklist => $"кроме {count}",
                SplitTunnelingMode.Whitelist => $"только {count}",
                _ => "все"
            };
        }

        /// <summary>The tunnel options take effect on the next connection; say so while they differ.</summary>
        private void Changed()
        {
            var pending = _vpn.State == VpnState.Connected
                && (WindowsPreferences.TunSettings != _appliedTun || WindowsPreferences.PreferUdpTransport != _appliedUdp);
            _reconnect.IsVisible = pending;
        }

        private Border BuildReconnectRow()
        {
            var action = Ui.Pill(Ui.PillText("Переподключить", Ui.C("HzOnGold")), Ui.C("HzGold"), Ui.C("HzGold"), new Thickness(14, 7));
            action.VerticalOptions = LayoutOptions.Center;
            action.GestureRecognizers.Add(Ui.Tap(async () =>
            {
                var server = _vpn.ActiveServer;
                try
                {
                    await _vpn.DisconnectAsync();
                    await _vpn.ConnectAsync(server);
                }
                catch (Exception ex)
                {
                    Diag.Warn("settings", $"reconnect failed: {ex.Message}");
                    await Dialog.Alert("Не удалось переподключиться", ex.Message);
                }
            }));

            var grid = new Grid
            {
                ColumnSpacing = 12,
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }
            };
            grid.Add(new VerticalStackLayout
            {
                Spacing = 2,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    Ui.Body("Изменения применятся после переподключения", 14),
                    Ui.Dim("Переподключение займёт несколько секунд и прервёт текущие соединения.", 12.5)
                }
            }, 0);
            grid.Add(action, 1);

            return new Border
            {
                Padding = new Thickness(16, 10),
                BackgroundColor = Ui.C("HzGoldSoft"),
                Stroke = new SolidColorBrush(Ui.C("HzGoldBorder")),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
                Content = grid,
                IsVisible = false,
            };
        }

        // ── Rows, in the shared screen's own shape ───────────────────────────

        private static View Section(string title, params View[] rows)
        {
            var stack = new VerticalStackLayout { Spacing = 0 };
            for (var i = 0; i < rows.Length; i++)
            {
                if (i > 0) stack.Children.Add(new BoxView { HeightRequest = 1, Color = Ui.C("HzBorderFaint") });
                stack.Children.Add(rows[i]);
            }
            var eyebrow = Ui.Eyebrow(title);
            eyebrow.FontSize = 10;
            eyebrow.Margin = new Thickness(4, 0);
            return new VerticalStackLayout
            {
                Spacing = 6,
                Children = { eyebrow, new Border { Style = Ui.S("HzListCard"), Content = stack } }
            };
        }

        private static View ToggleRow(string title, string detail, bool value, Action<bool> set)
        {
            var toggle = new PillToggle { IsToggled = value, VerticalOptions = LayoutOptions.Center };
            toggle.Toggled += (_, e) => set(e.Value);
            return Row(title, detail, toggle, null);
        }

        private static View LinkRow(string title, string detail, Label value, Action open)
        {
            value.VerticalOptions = LayoutOptions.Center;
            var trailing = new HorizontalStackLayout
            {
                Spacing = 8,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    value,
                    new IconView { Kind = IconKind.ChevronRight, IconSize = 15, StrokeWidth = 2, Color = Ui.C("HzTextMuted"), VerticalOptions = LayoutOptions.Center }
                }
            };
            return Row(title, detail, trailing, open);
        }

        private static View Row(string title, string detail, View trailing, Action? tap)
        {
            var name = Ui.Body(title);
            name.FontAttributes = FontAttributes.Bold;
            var grid = new Grid
            {
                Padding = new Thickness(16, 10),
                ColumnSpacing = 12,
                MinimumHeightRequest = 56,
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }
            };
            grid.Add(new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center, Children = { name, Ui.Dim(detail, 12) } }, 0);
            grid.Add(trailing, 1);
            if (tap is not null) grid.GestureRecognizers.Add(Ui.Tap(tap));
            return grid;
        }
    }
}
