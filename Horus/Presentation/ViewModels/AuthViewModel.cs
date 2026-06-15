using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Horus.Domain.Interfaces;

namespace Horus.Presentation.ViewModels
{
    public partial class AuthViewModel : ObservableObject
    {
        private readonly IAuthService _authService;

        [ObservableProperty] private string _login = string.Empty;
        [ObservableProperty] private string _password = string.Empty;
        [ObservableProperty] private bool _isLoading;
        [ObservableProperty] private bool _hasError;
        [ObservableProperty] private string _errorMessage = string.Empty;

        public AuthViewModel(IAuthService authService)
        {
            _authService = authService;
        }

        [RelayCommand]
        private async Task LoginAsync()
        {
            if (string.IsNullOrWhiteSpace(Login) || string.IsNullOrWhiteSpace(Password))
            {
                ErrorMessage = "Please enter your login and password.";
                HasError = true;
                return;
            }

            HasError = false;
            IsLoading = true;

            try
            {
                var result = await _authService.LoginAsync(Login, Password);
                if (!result.Success)
                {
                    ErrorMessage = result.Message ?? "Authentication failed.";
                    HasError = true;
                    return;
                }

                await Shell.Current.GoToAsync("//MainPage");
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                HasError = true;
            }
            finally
            {
                IsLoading = false;
            }
        }
    }
}
