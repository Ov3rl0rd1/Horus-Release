using System.Collections.Specialized;
using System.ComponentModel;
using Horus.Domain.Models;
using Horus.Presentation.ViewModels;
using Microsoft.Maui.Controls.Shapes;

namespace Horus.Platforms.Windows.Views
{
    /// <summary>
    /// The Windows home: the connect button, the server it will use, and a real speed graph
    /// with the round trip to the node underneath. Nothing else.
    ///
    /// <para>The shared desktop home carried an IP card, a recommended-servers list and a
    /// speed graph whose bars were a single level with random jitter. For a player the
    /// questions are "am I on", "which server", "how fast" and "what is my ping"; everything
    /// else moved to where it belongs (servers to their screen, applications to theirs).</para>
    ///
    /// <para>Notices stay — an expired subscription or a waiting update is something the user
    /// has to act on — but as a compact row above the button rather than cards.</para>
    /// </summary>
    internal sealed class DesktopHomeView : ContentView
    {
        private readonly MainViewModel _vm;
        private readonly HomeTelemetry _telemetry;

        private readonly VerticalStackLayout _notices = new() { Spacing = 8 };
        private readonly Border _publisher;
        private readonly Ellipse _spinRing;
        private readonly TrafficGraphView _graph;
        private readonly LatencyStripView _strip;
        private readonly Label _down = Ui.Strong(null, 19);
        private readonly Label _up = Ui.Strong(null, 19);
        private readonly Label _ping = Ui.Strong(null, 19);
        private readonly Label _pingDetail = Ui.Dim(null, 12.5);
        private readonly Label _graphEmpty = Ui.Dim("Подключитесь — здесь появится скорость и пинг до сервера", 13);

        private CancellationTokenSource? _spin;
        private bool _visible;

        public DesktopHomeView(MainViewModel vm, HomeTelemetry telemetry)
        {
            _vm = vm;
            _telemetry = telemetry;
            BindingContext = vm;

            _graph = new TrafficGraphView(telemetry.Traffic);
            _strip = new LatencyStripView(telemetry.Latency);
            _spinRing = new Ellipse
            {
                WidthRequest = 236,
                HeightRequest = 236,
                StrokeThickness = 2.5,
                Stroke = new SolidColorBrush(Ui.C("HzGold")),
                StrokeDashArray = new DoubleCollection { 18, 60 },
                InputTransparent = true,
            };
            _spinRing.SetBinding(OpacityProperty, nameof(MainViewModel.SpinOpacity));
            _publisher = BuildPublisherRow();

            var column = new VerticalStackLayout
            {
                Spacing = 18,
                Padding = new Thickness(34, 26, 34, 30),
                MaximumWidthRequest = 860,
                HorizontalOptions = LayoutOptions.Fill,
                Children = { _publisher, _notices, BuildConnect(), BuildGraphCard() }
            };
            Content = new ScrollView { Content = new Grid { Children = { column } } };
            column.HorizontalOptions = LayoutOptions.Center;

            _vm.PropertyChanged += OnVmChanged;
            _vm.Notices.CollectionChanged += OnNoticesChanged;
            _telemetry.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(RefreshTelemetry);

            RebuildNotices();
            RefreshTelemetry();
            UpdateSpin();
        }

        /// <summary>Called by the screen host: the graph and the probe run only while visible.</summary>
        public void SetVisible(bool visible)
        {
            _visible = visible;
            _telemetry.SetVisible(visible);
            _graph.SetRunning(visible && _vm.IsOn);
            if (visible) RefreshTelemetry();
        }

        // ── Connect ──────────────────────────────────────────────────────────

        private View BuildConnect()
        {
            var dot = new Ellipse { WidthRequest = 8, HeightRequest = 8, VerticalOptions = LayoutOptions.Center };
            dot.SetBinding(Shape.FillProperty, nameof(MainViewModel.StatusColor), converter: new BrushOf());
            var statusText = new Label { FontSize = 13.5, FontAttributes = FontAttributes.Bold, CharacterSpacing = 0.5, FontFamily = Ui.F("FontBold") };
            statusText.SetBinding(Label.TextProperty, nameof(MainViewModel.StatusText));
            statusText.SetBinding(Label.TextColorProperty, nameof(MainViewModel.StatusColor));
            var pill = new Border
            {
                Padding = new Thickness(17, 9),
                HorizontalOptions = LayoutOptions.Center,
                StrokeShape = new RoundRectangle { CornerRadius = 99 },
                Content = new HorizontalStackLayout { Spacing = 9, Children = { dot, statusText } }
            };
            pill.SetBinding(BackgroundColorProperty, nameof(MainViewModel.PillBg));
            pill.SetBinding(Border.StrokeProperty, nameof(MainViewModel.PillBorder), converter: new BrushOf());

            var protocol = Ui.Dim(null, 11);
            protocol.HorizontalTextAlignment = TextAlignment.Center;
            protocol.SetBinding(Label.TextProperty, nameof(MainViewModel.ActiveProtocolLabel));
            protocol.SetBinding(IsVisibleProperty, nameof(MainViewModel.ShowActiveProtocol));

            // The button.
            var logo = new Image { Source = "logo_gold.png", WidthRequest = 84, HorizontalOptions = LayoutOptions.Center };
            logo.SetBinding(OpacityProperty, nameof(MainViewModel.LogoOpacity));
            var word = new Label
            {
                FontSize = 13.5, FontAttributes = FontAttributes.Bold, CharacterSpacing = 2.5,
                FontFamily = Ui.F("FontBold"), HorizontalOptions = LayoutOptions.Center
            };
            word.SetBinding(Label.TextProperty, nameof(MainViewModel.BtnLabel));
            word.SetBinding(Label.TextColorProperty, nameof(MainViewModel.BtnWordColor));
            var face = new Border
            {
                WidthRequest = 206,
                HeightRequest = 206,
                StrokeThickness = 1,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                StrokeShape = new RoundRectangle { CornerRadius = 103 },
                Background = new LinearGradientBrush(
                    new GradientStopCollection { new GradientStop(Ui.C("HzRingTop"), 0), new GradientStop(Ui.C("HzRingBottom"), 1) },
                    new Point(0, 0), new Point(1, 1)),
                Content = new VerticalStackLayout
                {
                    Spacing = 12,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    Children = { logo, word }
                }
            };
            face.SetBinding(Border.StrokeProperty, nameof(MainViewModel.RingBorder), converter: new BrushOf());
            face.GestureRecognizers.Add(new TapGestureRecognizer { Command = _vm.ToggleConnectCommand });
            var ring = new Grid
            {
                WidthRequest = 250,
                HeightRequest = 250,
                HorizontalOptions = LayoutOptions.Center,
                Margin = new Thickness(0, 10, 0, 4),
                Children =
                {
                    new Ellipse
                    {
                        WidthRequest = 250, HeightRequest = 250, StrokeThickness = 1,
                        Stroke = new SolidColorBrush(Ui.C("HzGoldBorder")), StrokeDashArray = new DoubleCollection { 2, 4 },
                        InputTransparent = true
                    },
                    _spinRing,
                    face
                }
            };

            var sub = Ui.Dim();
            sub.HorizontalTextAlignment = TextAlignment.Center;
            sub.SetBinding(Label.TextProperty, nameof(MainViewModel.StatusSub));
            // DEV-PANEL: tap target for the pre-release protocol picker, as on the shared home.
            sub.GestureRecognizers.Add(new TapGestureRecognizer { Command = _vm.DevPanelCommand });
            var devBadge = Ui.Dim(null, 11);
            devBadge.HorizontalTextAlignment = TextAlignment.Center;
            devBadge.TextColor = Ui.C("HzGoldLight");
            devBadge.SetBinding(Label.TextProperty, nameof(MainViewModel.DevBadge));
            devBadge.SetBinding(IsVisibleProperty, nameof(MainViewModel.DevBadge), converter: new NotEmpty());

            var timer = new Label
            {
                FontSize = 30, TextColor = Ui.C("HzGoldLight"), CharacterSpacing = 1,
                HorizontalOptions = LayoutOptions.Center, FontFamily = Ui.F("FontBody")
            };
            timer.SetBinding(Label.TextProperty, nameof(MainViewModel.SessionDuration));
            timer.SetBinding(IsVisibleProperty, nameof(MainViewModel.ShowTimer));

            return new VerticalStackLayout
            {
                Spacing = 6,
                HorizontalOptions = LayoutOptions.Center,
                Children = { pill, protocol, ring, sub, devBadge, timer, BuildServerChip() }
            };
        }

        private View BuildServerChip()
        {
            var code = new Label
            {
                TextColor = Ui.C("HzGoldLight"), FontSize = 11.5, FontAttributes = FontAttributes.Bold,
                FontFamily = Ui.F("FontBold"), HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center
            };
            code.SetBinding(Label.TextProperty, nameof(MainViewModel.CurCode));
            var badge = new Border
            {
                WidthRequest = 40, HeightRequest = 40,
                BackgroundColor = Ui.C("HzGoldSoft"), Stroke = new SolidColorBrush(Ui.C("HzGoldBorder")),
                StrokeShape = new RoundRectangle { CornerRadius = 12 },
                Content = code
            };
            var name = Ui.Body();
            name.FontAttributes = FontAttributes.Bold;
            name.SetBinding(Label.TextProperty, nameof(MainViewModel.CurName));
            var sub = Ui.Dim(null, 13);
            sub.SetBinding(Label.TextProperty, nameof(MainViewModel.CurSub));
            var change = Ui.Dim("Сменить", 13);
            change.VerticalOptions = LayoutOptions.Center;

            var grid = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                ColumnSpacing = 12,
            };
            grid.Add(badge, 0);
            grid.Add(new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center, Children = { name, sub } }, 1);
            grid.Add(change, 2);

            var card = Ui.Card(grid, new Thickness(16, 12));
            card.Margin = new Thickness(0, 14, 0, 0);
            card.WidthRequest = 420;
            card.GestureRecognizers.Add(new TapGestureRecognizer { Command = _vm.GoServersCommand });
            return card;
        }

        // ── Graph ────────────────────────────────────────────────────────────

        private View BuildGraphCard()
        {
            static View Rate(string caption, Color dot, Label value) => new VerticalStackLayout
            {
                Spacing = 2,
                Children =
                {
                    new HorizontalStackLayout
                    {
                        Spacing = 6,
                        Children =
                        {
                            new Ellipse { WidthRequest = 7, HeightRequest = 7, Fill = new SolidColorBrush(dot), VerticalOptions = LayoutOptions.Center },
                            Ui.Eyebrow(caption)
                        }
                    },
                    value
                }
            };

            var pingBlock = new VerticalStackLayout
            {
                Spacing = 2,
                HorizontalOptions = LayoutOptions.End,
                Children = { Ui.Eyebrow("Пинг до сервера"), _ping, _pingDetail }
            };
            _ping.HorizontalTextAlignment = TextAlignment.End;
            _pingDetail.HorizontalTextAlignment = TextAlignment.End;

            var header = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
                ColumnSpacing = 34,
            };
            header.Add(Rate("Загрузка", Ui.C("HzGold"), _down), 0);
            header.Add(Rate("Отдача", Ui.C("NeonViolet"), _up), 1);
            header.Add(pingBlock, 2);

            _graphEmpty.HorizontalTextAlignment = TextAlignment.Center;
            _graphEmpty.HorizontalOptions = LayoutOptions.Center;
            _graphEmpty.VerticalOptions = LayoutOptions.Center;
            var graphHost = new Grid { Children = { _graph, _graphEmpty } };

            var stripCaption = Ui.Dim("Каждая полоска — замер раз в 2 с; красная метка — ответа не было", 11.5);

            return Ui.Card(new VerticalStackLayout
            {
                Spacing = 14,
                Children = { header, graphHost, _strip, stripCaption }
            });
        }

        private void RefreshTelemetry()
        {
            var on = _vm.IsOn;
            _graphEmpty.IsVisible = !on;
            _graph.SetRunning(_visible && on);
            if (_visible)
            {
                _graph.Invalidate();
                _strip.Invalidate();
            }
            _down.Text = on ? _vm.DownloadSpeed : "—";
            _up.Text = on ? _vm.UploadSpeed : "—";

            var latency = _telemetry.Latency;
            if (!on)
            {
                _ping.Text = "—";
                _ping.TextColor = Ui.C("HzText");
                _pingDetail.Text = " ";
                return;
            }

            if (latency.Typical is { } ms)
            {
                _ping.Text = $"{ms} мс";
                _ping.TextColor = LatencyStripView.ColorFor(ms, Ui.C("HzGreen"), Ui.C("HzGold"), Ui.C("HzRed"));
                var jitter = latency.Jitter is { } j ? $"джиттер {j} мс" : "джиттер —";
                _pingDetail.Text = $"{jitter} · потери {latency.Loss:P0}";
            }
            else
            {
                _ping.Text = "…";
                _ping.TextColor = Ui.C("HzTextDim");
                _pingDetail.Text = latency.Answering ? "нет ответа" : "измеряем";
            }
        }

        // ── Notices ──────────────────────────────────────────────────────────

        private void OnNoticesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
            MainThread.BeginInvokeOnMainThread(RebuildNotices);

        /// <summary>At most two notices exist at a time (INoticeService), so rebuilding is cheap.</summary>
        private void RebuildNotices()
        {
            _notices.Children.Clear();
            foreach (var notice in _vm.Notices.ToList()) _notices.Children.Add(NoticeRow(notice));
            _notices.IsVisible = _notices.Children.Count > 0;
        }

        private View NoticeRow(AppNotice notice)
        {
            var problem = notice.Tone == NoticeTone.Problem;
            var accent = problem ? Ui.C("HzRed") : Ui.C("HzGoldLight");

            var title = new Label { Text = notice.Title, TextColor = accent, FontSize = 14, FontAttributes = FontAttributes.Bold, FontFamily = Ui.F("FontBody") };
            var text = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center, Children = { title, Ui.Dim(notice.Message, 12.5) } };
            if (notice.HasProgress)
                text.Children.Add(new ProgressBar { Progress = notice.ProgressValue, ProgressColor = Ui.C("HzGold"), HeightRequest = 3, Margin = new Thickness(0, 6, 0, 1) });

            var grid = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) },
                ColumnSpacing = 10,
            };
            grid.Add(text, 0);
            if (notice.HasAction)
            {
                var action = Ui.Pill(Ui.PillText(notice.ActionLabel, Ui.C("HzOnGold")), Ui.C("HzGold"), Ui.C("HzGold"), new Thickness(13, 7));
                action.VerticalOptions = LayoutOptions.Center;
                action.GestureRecognizers.Add(Ui.Tap(() => _vm.ActOnNoticeCommand.Execute(notice)));
                grid.Add(action, 1);
            }
            if (notice.CanDismiss)
            {
                var close = new Label { Text = "✕", TextColor = Ui.C("HzTextMuted"), FontSize = 15, VerticalOptions = LayoutOptions.Center, Padding = new Thickness(6, 0, 0, 0) };
                close.GestureRecognizers.Add(Ui.Tap(() => _vm.DismissNoticeCommand.Execute(notice)));
                grid.Add(close, 2);
            }

            return new Border
            {
                Padding = new Thickness(16, 11),
                BackgroundColor = problem ? Ui.C("HzRedSoft") : Ui.C("HzGoldSoft"),
                Stroke = new SolidColorBrush(problem ? Ui.C("HzRedBorder") : Ui.C("HzGoldBorder")),
                StrokeShape = new RoundRectangle { CornerRadius = 14 },
                Content = grid
            };
        }

        private Border BuildPublisherRow()
        {
            var install = new Button { Text = "Установить", Style = Ui.S("HzSecondaryButton"), VerticalOptions = LayoutOptions.Center, Command = _vm.InstallCertificateCommand };
            install.SetBinding(IsEnabledProperty, nameof(MainViewModel.IsInstallingCertificate), converter: new Not());
            var grid = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                ColumnSpacing = 14,
            };
            grid.Add(new VerticalStackLayout
            {
                Spacing = 2,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    Ui.Strong("Издатель не подтверждён", 14),
                    Ui.Dim("Horus подписан собственным сертификатом. Установите его, чтобы Windows перестала считать издателя неизвестным.", 12.5)
                }
            }, 0);
            grid.Add(install, 1);
            var card = Ui.Card(grid, new Thickness(18, 12));
            card.SetBinding(IsVisibleProperty, nameof(MainViewModel.ShowPublisherWarning));
            return card;
        }

        // ── State ────────────────────────────────────────────────────────────

        private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.VpnState) or "" or null:
                    UpdateSpin();
                    RefreshTelemetry();
                    break;
                case nameof(MainViewModel.DownloadSpeed) or nameof(MainViewModel.UploadSpeed):
                    _down.Text = _vm.IsOn ? _vm.DownloadSpeed : "—";
                    _up.Text = _vm.IsOn ? _vm.UploadSpeed : "—";
                    break;
            }
        }

        private void UpdateSpin()
        {
            _spin?.Cancel();
            _spin = null;
            _spinRing.Rotation = 0;
            if (!_vm.IsConnecting) return;

            var cts = new CancellationTokenSource();
            _spin = cts;
            _ = SpinAsync(cts.Token);
        }

        private async Task SpinAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
                await _spinRing.RotateToAsync(_spinRing.Rotation + 360, 1100, Easing.Linear);
        }

        // ── Converters ───────────────────────────────────────────────────────

        private sealed class BrushOf : IValueConverter
        {
            public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
                value is Color c ? new SolidColorBrush(c) : null;

            public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
                throw new NotSupportedException();
        }

        private sealed class Not : IValueConverter
        {
            public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is not true;

            public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
                throw new NotSupportedException();
        }

        private sealed class NotEmpty : IValueConverter
        {
            public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
                value is string s && s.Length > 0;

            public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
                throw new NotSupportedException();
        }
    }
}
