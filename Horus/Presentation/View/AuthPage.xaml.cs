using Horus.Presentation.ViewModels;

namespace Horus.Presentation.View
{
    public partial class AuthPage : ContentPage
    {
        private readonly AuthViewModel _vm;

        public AuthPage(AuthViewModel viewModel)
        {
            InitializeComponent();
            _vm = viewModel;
            BindingContext = viewModel;
            viewModel.PropertyChanged += OnVmPropertyChanged;

            Shell.SetNavBarIsVisible(this, false);
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await PlayEntranceAnimationAsync();
        }

        private async Task PlayEntranceAnimationAsync()
        {
            // Start from invisible and offset downward
            this.Opacity = 0;
            this.TranslationY = 30;
            await Task.WhenAll(
                this.FadeToAsync(1, 400, Easing.CubicOut),
                this.TranslateToAsync(0, 0, 400, Easing.CubicOut));
        }

        protected override bool OnBackButtonPressed()
        {
            return true;
        }

        private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AuthViewModel.HasError) && _vm.HasError)
                _ = ShakeLoginButtonAsync();
        }

        private async Task ShakeLoginButtonAsync()
        {
            for (int i = 0; i < 3; i++)
            {
                await BtnLogin.TranslateToAsync(-8, 0, 50);
                await BtnLogin.TranslateToAsync(8, 0, 50);
            }
            await BtnLogin.TranslateToAsync(0, 0, 50);
        }

        private void OnTogglePasswordTapped(object sender, TappedEventArgs e)
        {
            EntryPassword.IsPassword = !EntryPassword.IsPassword;
            LblTogglePassword.Text = EntryPassword.IsPassword ? "👁" : "🙈";
        }

        private async void OnForgotPasswordTapped(object sender, TappedEventArgs e)
        {
            await DisplayAlertAsync("Reset Password",
                "Password reset link will be sent to your email.", "OK");
        }

        private async void OnRegisterTapped(object sender, TappedEventArgs e)
        {
            await Shell.Current.GoToAsync("RegisterPage");
        }
    }
}
