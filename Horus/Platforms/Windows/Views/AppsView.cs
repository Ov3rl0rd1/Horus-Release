using System.ComponentModel;
using System.Runtime.Versioning;
using Horus.Domain.Models;
using Horus.Platforms.Windows.Tunnel;
using Horus.Presentation.Navigation;
using Horus.Presentation.View.Controls;
using Microsoft.Maui.Controls.Shapes;

namespace Horus.Platforms.Windows.Views
{
    /// <summary>
    /// "Приложения": choose how the tunnel treats applications, see where each one's traffic
    /// actually goes, and make an application reconnect so a changed rule reaches it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class AppsView : ContentView
    {
        private readonly AppsViewModel _vm;
        private readonly Dictionary<SplitTunnelingMode, Border> _modeCards = [];
        private readonly Label _summary = Ui.Dim(null, 13);
        private readonly Border _staleBanner;
        private readonly Label _staleText = Ui.Body(null, 14);
        private readonly Label _empty = Ui.Dim("Ничего не найдено", 14);
        private readonly Label _modeHint = Ui.Dim(null, 12.5);

        public AppsView(AppsViewModel vm)
        {
            _vm = vm;
            BindingContext = vm;

            _staleBanner = BuildStaleBanner();

            var list = new CollectionView
            {
                ItemsSource = vm.Visible,
                SelectionMode = SelectionMode.None,
                ItemTemplate = new DataTemplate(() => new AppRowView(_vm)),
                ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical) { ItemSpacing = 6 },
            };

            _empty.HorizontalOptions = LayoutOptions.Center;
            _empty.Margin = new Thickness(0, 30, 0, 0);
            _empty.IsVisible = false;

            var root = new Grid
            {
                Padding = new Thickness(30, 20, 34, 18),
                RowSpacing = 14,
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star),
                }
            };
            root.Add(new VerticalStackLayout { Spacing = 4, Children = { Ui.Title("Приложения"), _summary } }, 0, 0);
            root.Add(BuildModeCard(), 0, 1);
            root.Add(BuildToolbar(), 0, 2);
            root.Add(_staleBanner, 0, 3);
            root.Add(new Grid { Children = { list, _empty } }, 0, 4);
            Content = root;

            _vm.PropertyChanged += OnVmChanged;
            _vm.Refreshed += (_, _) => _empty.IsVisible = _vm.Visible.Count == 0;
            Render();
        }

        public void SetVisible(bool visible) => _vm.SetVisible(visible);

        // ── Mode ─────────────────────────────────────────────────────────────

        private View BuildModeCard()
        {
            var cards = new Grid
            {
                ColumnSpacing = 10,
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) }
            };
            cards.Add(ModeOption(SplitTunnelingMode.Disabled, "Всё через VPN", "Весь трафик компьютера идёт через VPN"), 0);
            cards.Add(ModeOption(SplitTunnelingMode.Blacklist, "Кроме выбранных", "Выбранные приложения работают напрямую, остальное — через VPN"), 1);
            cards.Add(ModeOption(SplitTunnelingMode.Whitelist, "Только выбранные", "Через VPN идут только выбранные приложения, остальное — напрямую"), 2);

            return new VerticalStackLayout
            {
                Spacing = 8,
                Children = { Ui.Eyebrow("Раздельное туннелирование"), cards, _modeHint }
            };
        }

        private View ModeOption(SplitTunnelingMode mode, string title, string text)
        {
            var card = new Border
            {
                Padding = new Thickness(14, 11),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 14 },
                Content = new VerticalStackLayout { Spacing = 3, Children = { Ui.Strong(title, 14.5), Ui.Dim(text, 12.5) } }
            };
            card.GestureRecognizers.Add(Ui.Tap(() => _vm.SetMode(mode)));
            _modeCards[mode] = card;
            return card;
        }

        // ── Toolbar ──────────────────────────────────────────────────────────

        private View BuildToolbar()
        {
            var search = new Entry { Placeholder = "Поиск по названию или файлу", Style = Ui.S("HzEntry"), HeightRequest = 42 };
            search.SetBinding(Entry.TextProperty, nameof(AppsViewModel.Search), BindingMode.TwoWay);
            var searchBox = new Border
            {
                Style = Ui.S("HzInputBorder"),
                Padding = new Thickness(12, 0),
                Content = new Grid
                {
                    ColumnSpacing = 8,
                    ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
                    Children = { new IconView { Kind = IconKind.Search, IconSize = 16, StrokeWidth = 2, Color = Ui.C("HzTextMuted"), VerticalOptions = LayoutOptions.Center } }
                }
            };
            ((Grid)searchBox.Content).Add(search, 1);

            var background = new PillToggle { VerticalOptions = LayoutOptions.Center };
            background.Toggled += (_, e) => _vm.ShowBackground = e.Value;
            var backgroundGroup = new HorizontalStackLayout
            {
                Spacing = 8,
                VerticalOptions = LayoutOptions.Center,
                Children = { background, new Label { Text = "Фоновые", Style = Ui.S("HzDim"), FontSize = 13, VerticalOptions = LayoutOptions.Center } }
            };

            var add = new Button { Text = "Добавить программу…", Style = Ui.S("HzSecondaryButton"), HeightRequest = 42, FontSize = 14, Padding = new Thickness(16, 0) };
            add.Clicked += async (_, _) => await _vm.AddExecutableAsync();

            var refresh = new Border
            {
                WidthRequest = 42, HeightRequest = 42, StrokeThickness = 1,
                Stroke = new SolidColorBrush(Ui.C("HzBorderStrong")), BackgroundColor = Colors.Transparent,
                StrokeShape = new RoundRectangle { CornerRadius = 12 },
                Content = new IconView { Kind = IconKind.Refresh, IconSize = 17, StrokeWidth = 2, Color = Ui.C("HzText"), HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center }
            };
            refresh.GestureRecognizers.Add(Ui.Tap(() => _ = _vm.RefreshNowAsync()));

            var bar = new Grid
            {
                ColumnSpacing = 12,
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto)
                }
            };
            bar.Add(searchBox, 0);
            bar.Add(backgroundGroup, 1);
            bar.Add(add, 2);
            bar.Add(refresh, 3);
            return bar;
        }

        private Border BuildStaleBanner()
        {
            var action = Ui.Pill(Ui.PillText("Перезапустить их", Ui.C("HzOnGold")), Ui.C("HzGold"), Ui.C("HzGold"), new Thickness(14, 7));
            action.VerticalOptions = LayoutOptions.Center;
            action.GestureRecognizers.Add(Ui.Tap(async () =>
            {
                var ok = await Dialog.Confirm("Перезапустить соединения?",
                    "Приложения переподключатся уже по новым правилам. Если среди них игра, сервер может отключить вас на несколько секунд.",
                    "Перезапустить", "Отмена");
                if (ok) _vm.RestartStale();
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
                Children = { _staleText, Ui.Dim("Открытые соединения сохраняют маршрут, с которым начались.", 12.5) }
            }, 0);
            grid.Add(action, 1);

            return new Border
            {
                Padding = new Thickness(16, 10),
                BackgroundColor = Ui.C("HzGoldSoft"),
                Stroke = new SolidColorBrush(Ui.C("HzGoldBorder")),
                StrokeShape = new RoundRectangle { CornerRadius = 14 },
                Content = grid,
                IsVisible = false,
            };
        }

        // ── State ────────────────────────────────────────────────────────────

        private void OnVmChanged(object? sender, PropertyChangedEventArgs e) => Render();

        private void Render()
        {
            foreach (var (mode, card) in _modeCards)
            {
                var on = _vm.Mode == mode;
                card.BackgroundColor = on ? Ui.C("HzGoldSoft") : Ui.C("HzSurface");
                card.Stroke = new SolidColorBrush(on ? Ui.C("HzGoldBorder") : Ui.C("HzBorder"));
            }

            _modeHint.Text = _vm.Mode switch
            {
                SplitTunnelingMode.Disabled => "Выберите режим, чтобы отметить приложения. Сайты из правил маршрутизации по-прежнему решаются по своим правилам.",
                _ => "Правило применяется сразу к новым соединениям. Уже открытые идут прежним путём — перезапустите их кнопкой у приложения."
            };
            _summary.Text = _vm.Summary;
            _staleText.Text = _vm.StaleCount == 1
                ? "У одного приложения соединения открыты до смены правила"
                : $"У {_vm.StaleCount} приложений соединения открыты до смены правила";
            _staleBanner.IsVisible = _vm.HasStale;
        }

        /// <summary>One application in the list.</summary>
        private sealed class AppRowView : ContentView
        {
            private readonly AppsViewModel _vm;
            private readonly Border _chip;
            private readonly Label _chipText;
            private readonly Border _restart;
            private readonly Label _restartText;
            private AppRow? _row;

            public AppRowView(AppsViewModel vm)
            {
                _vm = vm;

                var image = new Image { WidthRequest = 30, HeightRequest = 30, Aspect = Aspect.AspectFit };
                image.SetBinding(Image.SourceProperty, nameof(AppRow.Icon));
                image.SetBinding(IsVisibleProperty, nameof(AppRow.HasIcon));
                var initial = new Label
                {
                    TextColor = Ui.C("HzGoldLight"), FontSize = 13, FontAttributes = FontAttributes.Bold, FontFamily = Ui.F("FontBold"),
                    HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center
                };
                initial.SetBinding(Label.TextProperty, nameof(AppRow.Initial));
                var tile = new Border
                {
                    WidthRequest = 34, HeightRequest = 34, StrokeThickness = 1,
                    BackgroundColor = Ui.C("HzSurface3"), Stroke = new SolidColorBrush(Ui.C("HzBorderStrong")),
                    StrokeShape = new RoundRectangle { CornerRadius = 9 },
                    Content = initial
                };
                tile.SetBinding(IsVisibleProperty, nameof(AppRow.HasIcon), converter: new Not());
                var icon = new Grid { WidthRequest = 36, HeightRequest = 36, VerticalOptions = LayoutOptions.Center, Children = { tile, image } };

                var name = Ui.Strong(null, 14.5);
                name.LineBreakMode = LineBreakMode.TailTruncation;
                name.SetBinding(Label.TextProperty, nameof(AppRow.Name));
                var sub = Ui.Dim(null, 12.5);
                sub.LineBreakMode = LineBreakMode.TailTruncation;
                sub.SetBinding(Label.TextProperty, nameof(AppRow.Subtitle));
                var text = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center, Children = { name, sub } };

                _chipText = Ui.PillText(null, Ui.C("HzText"), 11.5);
                _chipText.SetBinding(Label.TextProperty, nameof(AppRow.RouteText));
                _chip = Ui.Pill(_chipText, Colors.Transparent, Colors.Transparent, new Thickness(10, 4));
                _chip.VerticalOptions = LayoutOptions.Center;
                _chip.SetBinding(IsVisibleProperty, nameof(AppRow.HasConnections));

                _restartText = Ui.PillText("Перезапустить", Ui.C("HzText"), 12);
                _restart = Ui.Pill(_restartText, Colors.Transparent, Ui.C("HzBorderStrong"), new Thickness(12, 6));
                _restart.VerticalOptions = LayoutOptions.Center;
                _restart.SetBinding(IsVisibleProperty, nameof(AppRow.HasConnections));
                _restart.GestureRecognizers.Add(Ui.Tap(OnRestart));

                var caption = new Label { Style = Ui.S("HzDim"), FontSize = 12.5, VerticalOptions = LayoutOptions.Center };
                caption.SetBinding(Label.TextProperty, nameof(AppRow.ChoiceCaption));
                var toggle = new PillToggle { VerticalOptions = LayoutOptions.Center };
                toggle.SetBinding(PillToggle.IsToggledProperty, nameof(AppRow.IsChosen));
                toggle.Toggled += (_, e) => { if (_row is not null) _vm.SetChosen(_row, e.Value); };
                var choice = new HorizontalStackLayout { Spacing = 8, VerticalOptions = LayoutOptions.Center, Children = { caption, toggle } };
                choice.SetBinding(IsVisibleProperty, nameof(AppRow.CanChoose));

                var grid = new Grid
                {
                    ColumnSpacing = 14,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto)
                    }
                };
                grid.Add(icon, 0);
                grid.Add(text, 1);
                grid.Add(_chip, 2);
                grid.Add(_restart, 3);
                grid.Add(choice, 4);

                Content = new Border
                {
                    Padding = new Thickness(14, 9),
                    BackgroundColor = Ui.C("HzSurface"),
                    Stroke = new SolidColorBrush(Ui.C("HzBorderFaint")),
                    StrokeThickness = 1,
                    StrokeShape = new RoundRectangle { CornerRadius = 12 },
                    MinimumHeightRequest = 56,
                    Content = grid
                };

                BindingContextChanged += (_, _) => Attach(BindingContext as AppRow);
            }

            private void Attach(AppRow? row)
            {
                if (_row is not null) _row.PropertyChanged -= OnRowChanged;
                _row = row;
                if (_row is not null) _row.PropertyChanged += OnRowChanged;
                Paint();
            }

            private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName is nameof(AppRow.Route) or nameof(AppRow.NeedsRestart)) Paint();
            }

            /// <summary>Colours that a binding cannot express without a converter per property.</summary>
            private void Paint()
            {
                var route = _row?.Route ?? RouteMix.Other;
                var (fg, bg, stroke) = route switch
                {
                    RouteMix.Vpn => (Ui.C("HzGreen"), Ui.C("HzGreenSoft"), Ui.C("HzGreenBorder")),
                    RouteMix.Direct => (Ui.C("HzGoldLight"), Ui.C("HzGoldSoft"), Ui.C("HzGoldBorder")),
                    RouteMix.Mixed => (Ui.C("NeonViolet"), Ui.C("HzSurface3"), Ui.C("NeonVioletDim")),
                    _ => (Ui.C("HzTextMuted"), Ui.C("HzSurface2"), Ui.C("HzBorder")),
                };
                _chipText.TextColor = fg;
                _chip.BackgroundColor = bg;
                _chip.Stroke = new SolidColorBrush(stroke);

                var urgent = _row?.NeedsRestart == true;
                _restart.BackgroundColor = urgent ? Ui.C("HzGold") : Colors.Transparent;
                _restart.Stroke = new SolidColorBrush(urgent ? Ui.C("HzGold") : Ui.C("HzBorderStrong"));
                _restartText.TextColor = urgent ? Ui.C("HzOnGold") : Ui.C("HzText");
            }

            private async void OnRestart()
            {
                if (_row is not { } row) return;
                var ok = await Dialog.Confirm($"Перезапустить соединения «{row.Name}»?",
                    "Приложение переподключится по текущим правилам. Если это игра, сервер может отключить вас на несколько секунд.",
                    "Перезапустить", "Отмена");
                if (ok) _vm.Restart(row);
            }
        }

        private sealed class Not : IValueConverter
        {
            public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is not true;

            public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
                throw new NotSupportedException();
        }
    }
}
