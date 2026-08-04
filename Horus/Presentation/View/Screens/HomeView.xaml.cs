using System.ComponentModel;
using Horus.Presentation.ViewModels;

namespace Horus.Presentation.View.Screens;

public partial class HomeView : ContentView
{
    private MainViewModel? _vm;
    private CancellationTokenSource? _animCts;

    public HomeView()
    {
        InitializeComponent();
        BindingContextChanged += OnBindingContextChanged;
    }

    private void OnBindingContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = BindingContext as MainViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            UpdateAnimation();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // VpnState drives every derived flag; refresh on any change.
        if (e.PropertyName is nameof(MainViewModel.VpnState) or "")
            UpdateAnimation();
    }

    private void UpdateAnimation()
    {
        _animCts?.Cancel();
        _animCts = new CancellationTokenSource();
        var token = _animCts.Token;

        SpinRing.Rotation = 0;
        GlowRing.Scale = 1;
        GlowHalo.Scale = 1;
        GlowHalo.Opacity = 0; // glow only shows while connected

        if (_vm is null) return;
        if (_vm.IsConnecting) _ = SpinAsync(token);
        else if (_vm.IsOn) _ = PulseAsync(token);
    }

    private async Task SpinAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
            await SpinRing.RotateToAsync(SpinRing.Rotation + 360, 1100, Easing.Linear);
    }

    private async Task PulseAsync(CancellationToken ct)
    {
        GlowHalo.Opacity = 0.35;
        while (!ct.IsCancellationRequested)
        {
            await Task.WhenAll(
                GlowRing.ScaleToAsync(1.05, 1200, Easing.SinInOut),
                GlowHalo.ScaleToAsync(1.15, 1200, Easing.SinInOut),
                GlowHalo.FadeToAsync(0.7, 1200, Easing.SinInOut));
            if (ct.IsCancellationRequested) break;
            await Task.WhenAll(
                GlowRing.ScaleToAsync(1.0, 1200, Easing.SinInOut),
                GlowHalo.ScaleToAsync(1.0, 1200, Easing.SinInOut),
                GlowHalo.FadeToAsync(0.35, 1200, Easing.SinInOut));
        }
        GlowRing.Scale = 1;
        GlowHalo.Scale = 1;
        GlowHalo.Opacity = 0;
    }
}
