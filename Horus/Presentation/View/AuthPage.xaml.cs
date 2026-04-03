using Horus.Presentation.ViewModels;

namespace Horus
{
    public partial class AuthPage : ContentPage
    {
        public AuthPage(AuthViewModel viewModel)
        {
            InitializeComponent();
            BindingContext = viewModel;
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
            // Navigate to registration page
            // await Shell.Current.GoToAsync(nameof(RegisterPage));
            await DisplayAlertAsync("Register", "Registration coming soon.", "OK");
        }
    }
}