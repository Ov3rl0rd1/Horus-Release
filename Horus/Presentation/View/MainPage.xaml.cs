using Horus.Domain.Models;
using Horus.Presentation.ViewModels;

namespace Horus.Presentation.View
{
    public partial class MainPage : ContentPage
    {
        private readonly MainViewModel _vm;
        private CancellationTokenSource? _animCts;

        public MainPage(MainViewModel viewModel)
        {
            InitializeComponent();
            _vm = viewModel;
            BindingContext = viewModel;
            viewModel.PropertyChanged += OnVmPropertyChanged;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await PlayEntranceAnimationAsync();

            if (!(_vm.VpnState == VpnState.Connected))
                ConnectionStatus_Changed(_vm.ConnectionStatus);
        }

        private async Task PlayEntranceAnimationAsync()
        {
            ContentStack.Opacity = 0;
            ContentStack.TranslationY = 40;
            await Task.WhenAll(
                ContentStack.FadeToAsync(1, 350, Easing.CubicOut),
                ContentStack.TranslateToAsync(0, 0, 350, Easing.CubicOut));
        }

        private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.ConnectionStatus))
                ConnectionStatus_Changed(_vm.ConnectionStatus);
        }

        private void ConnectionStatus_Changed(string status)
        {
            _animCts?.Cancel();
            _animCts = new CancellationTokenSource();
            var token = _animCts.Token;

            switch (status)
            {
                case "Connected":
                    _ = PulseOuterGlowAsync(token);
                    break;

                case "Connecting":
                    _ = SpinStrokeRingAsync(token);
                    break;

                default:
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        OuterGlowRing.Scale = 1;
                        StrokeRing.Rotation = 0;
                    });
                    break;
            }
        }

        private async Task PulseOuterGlowAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await OuterGlowRing.ScaleToAsync(1.08, 800, Easing.SinInOut);
                if (ct.IsCancellationRequested) break;
                await OuterGlowRing.ScaleToAsync(1.0, 800, Easing.SinInOut);
            }
            OuterGlowRing.Scale = 1;
        }

        private async Task SpinStrokeRingAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await StrokeRing.RotateToAsync(StrokeRing.Rotation + 360, 1200, Easing.Linear);
            }
        }

        private async void OnConnectTapped(object sender, TappedEventArgs e)
        {
            if (_vm.VpnState == VpnState.Connected || _vm.VpnState == VpnState.Connecting)
                await _vm.DisconnectCommand.ExecuteAsync(null);
            else if (_vm.VpnState == VpnState.Disconnected)
                await _vm.ConnectCommand.ExecuteAsync(null);
        }
    }
}
