using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Horus.Domain.Interfaces;

namespace Horus.Presentation.ViewModels
{
    public partial class RegisterViewModel : ObservableObject
    {
        private readonly IAuthService _auth;

        [ObservableProperty] private string _username = string.Empty;
        [ObservableProperty] private string _email = string.Empty;
        [ObservableProperty] private string _password = string.Empty;
        [ObservableProperty] private string _passwordConfirm = string.Empty;
        [ObservableProperty] private bool _isLoading;
        [ObservableProperty] private bool _hasError;
        [ObservableProperty] private string _errorMessage = string.Empty;
        [ObservableProperty] private bool _isPasswordVisible;
        [ObservableProperty] private bool _isPasswordConfirmVisible;

        public RegisterViewModel(IAuthService auth)
        {
            _auth = auth;
        }

        [RelayCommand]
        async Task RegisterAsync()
        {
            HasError = false;

            if (string.IsNullOrWhiteSpace(Username))
            {
                ShowError("Please enter a username.");
                return;
            }
            if (string.IsNullOrWhiteSpace(Email) || !Email.Contains('@'))
            {
                ShowError("Please enter a valid email address.");
                return;
            }
            if (Password.Length < 8)
            {
                ShowError("Password must be at least 8 characters.");
                return;
            }
            if (Password != PasswordConfirm)
            {
                ShowError("Passwords do not match.");
                return;
            }

            IsLoading = true;
            try
            {
                var result = await _auth.RegisterAsync(Username.Trim(), Email.Trim(), Password);
                if (result.Success)
                {
                    await Shell.Current.GoToAsync("//MainPage");
                }
                else
                {
                    ShowError(result.Message ?? "Registration failed. Please try again.");
                }
            }
            catch (Exception ex)
            {
                ShowError($"Connection error: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        void TogglePasswordVisibility() => IsPasswordVisible = !IsPasswordVisible;

        [RelayCommand]
        void TogglePasswordConfirmVisibility() => IsPasswordConfirmVisible = !IsPasswordConfirmVisible;

        [RelayCommand]
        async Task GoToLoginAsync() => await Shell.Current.GoToAsync("..");

        private void ShowError(string message)
        {
            ErrorMessage = message;
            HasError = true;
        }
    }
}
