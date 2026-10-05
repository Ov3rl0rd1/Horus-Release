using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using Horus.Presentation.Navigation;

namespace Horus.Presentation.ViewModels
{
    /// <summary>
    /// Drives the shared auth flow (login → register → confirm email → reset),
    /// mirroring the design's single set of fields carried across screens.
    ///
    /// All four steps talk to HorusAPI v1: register mails a 6-digit code and does
    /// <b>not</b> sign the user in — <c>POST /auth/verify</c> is what issues the session.
    /// </summary>
    public partial class AuthFlowViewModel : ObservableObject
    {
        private readonly IAuthService _auth;
        private readonly Navigator _nav;

        public AuthFlowViewModel(IAuthService auth, Navigator nav)
        {
            _auth = auth;
            _nav = nav;
        }

        // ── Shared fields (carried across every auth screen) ──
        [ObservableProperty] private string _email = string.Empty;
        [ObservableProperty] private string _username = string.Empty;
        [ObservableProperty] private string _password = string.Empty;
        [ObservableProperty] private string _code = string.Empty;

        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private bool _hasError;
        [ObservableProperty] private string _errorMessage = string.Empty;
        [ObservableProperty] private bool _resetSent;

        /// <summary>Seconds until the mailed code expires; drives the resend hint.</summary>
        [ObservableProperty] private int _codeExpiresInSeconds;

        /// <summary>
        /// Confirmation ticket — <b>not</b> a session. Handed out by register and by login's
        /// <c>403 email_unverified</c>; it identifies the account to verify and resend-code,
        /// which is the only way to confirm after signing in by username. Lives 30 minutes.
        /// </summary>
        private string? _pendingToken;

        /// <summary>The address as the API showed it (masked when the user signed in by username).</summary>
        private string _emailMasked = string.Empty;

        /// <summary>Confirm was reached from Login rather than Register — that is where "back" goes.</summary>
        private bool _confirmFromLogin;

        // Validity flags drive button enable/opacity (recomputed on field change).
        [ObservableProperty] private bool _canLogin;
        [ObservableProperty] private bool _canRegister;
        [ObservableProperty] private bool _canConfirm;
        [ObservableProperty] private bool _canReset;

        public string EmailShown =>
            EmailValid(Email) ? Email : !string.IsNullOrEmpty(_emailMasked) ? _emailMasked : "ваш email";

        partial void OnEmailChanged(string value) { Recompute(); OnPropertyChanged(nameof(EmailShown)); }
        partial void OnPasswordChanged(string value) => Recompute();
        partial void OnUsernameChanged(string value) => Recompute();
        partial void OnCodeChanged(string value)
        {
            var digits = Regex.Replace(value ?? string.Empty, "\\D", "");
            if (digits.Length > 6) digits = digits[..6];
            if (digits != value) { Code = digits; return; }
            Recompute();
        }

        private void Recompute()
        {
            // The login field is labelled "email" in the design; the API matches the value
            // against both the username and the address, so either works.
            CanLogin = Email.Trim().Length > 0 && Password.Length > 0;
            CanRegister = EmailValid(Email) && Username.Trim().Length >= 3 && Password.Length >= 8;
            CanConfirm = Regex.IsMatch(Code, "^\\d{6}$");
            CanReset = EmailValid(Email);
        }

        private static bool EmailValid(string s) => Regex.IsMatch(s ?? string.Empty, "\\S+@\\S+\\.\\S+");

        // ── Navigation between auth screens ──
        [RelayCommand] private void GoOnboarding() { ClearError(); _nav.Go(AppScreen.Onboarding); }
        [RelayCommand] private void GoLogin() { ClearError(); _nav.Go(AppScreen.Login); }
        [RelayCommand] private void GoRegister() { ClearError(); _nav.Go(AppScreen.Register); }
        [RelayCommand] private void GoReset() { ClearError(); ResetSent = false; _nav.Go(AppScreen.Reset); }
        [RelayCommand] private void BackFromConfirm() { if (_confirmFromLogin) GoLogin(); else GoRegister(); }

        // ── Login ──
        [RelayCommand]
        private async Task DoLoginAsync()
        {
            if (!CanLogin || IsBusy) return;
            ClearError();
#if DEBUG
            if (AppConfiguration.UseDevBypass)
            {
                _auth.DevSignIn(Email);
                _nav.Reset(AppScreen.Home);
                return;
            }
#endif
            IsBusy = true;
            try
            {
                var result = await _auth.LoginAsync(Email.Trim(), Password);

                // The account exists but the address was never confirmed: continue on the
                // code screen with the ticket the API handed back. Signing in by username
                // means the full address was never typed here — the masked one is shown.
                if (result.ErrorCode == ErrorCodes.EmailUnverified && result.Pending is { } pending)
                {
                    _pendingToken = pending.PendingToken;
                    _emailMasked = pending.EmailMasked;
                    _confirmFromLogin = true;
                    CodeExpiresInSeconds = pending.CodeExpiresInSeconds;
                    Code = string.Empty;
                    OnPropertyChanged(nameof(EmailShown));
                    _nav.Go(AppScreen.Confirm);
                    // Login mails nothing by itself. Zero means the last code is gone too, so
                    // the only way forward is a new one — say so rather than wait for a guess.
                    if (pending.CodeExpiresInSeconds <= 0)
                        ShowError("Код из прошлого письма уже не действует. Нажмите «Отправить код ещё раз».");
                    return;
                }

                if (!result.Success)
                {
                    ShowError(result.Message);
                    return;
                }
                Password = string.Empty;
                _nav.Reset(AppScreen.Home);
            }
            catch (Exception ex)
            {
                ShowError($"Ошибка сети: {ex.Message}");
            }
            finally { IsBusy = false; }
        }

        // ── Register → mails a confirmation code, no session yet ──
        [RelayCommand]
        private async Task DoRegisterAsync()
        {
            if (!CanRegister || IsBusy) return;
            ClearError();
#if DEBUG
            if (AppConfiguration.UseDevBypass)
            {
                _auth.DevSignIn(Email);
                Code = string.Empty;
                _nav.Go(AppScreen.Confirm);
                return;
            }
#endif
            if (AccountRules.RegistrationProblem(Username.Trim(), Password) is { } problem)
            {
                ShowError(problem);
                return;
            }

            IsBusy = true;
            try
            {
                var result = await _auth.RegisterAsync(Username.Trim(), Email.Trim(), Password);
                if (!result.Success)
                {
                    ShowError(result.Message ?? "Не удалось создать аккаунт.");
                    return;
                }

                CodeExpiresInSeconds = result.CodeExpiresInSeconds;
                _pendingToken = result.PendingToken;
                _emailMasked = string.Empty;
                _confirmFromLogin = false;
                Code = string.Empty;
                _nav.Go(AppScreen.Confirm);
            }
            catch (Exception ex)
            {
                ShowError($"Ошибка сети: {ex.Message}");
            }
            finally { IsBusy = false; }
        }

        // ── Confirm email → exchanges the code for a session ──
        [RelayCommand]
        private async Task DoConfirmAsync()
        {
            if (!CanConfirm || IsBusy) return;
            ClearError();
#if DEBUG
            if (AppConfiguration.UseDevBypass)
            {
                _nav.Reset(AppScreen.Home);
                return;
            }
#endif
            IsBusy = true;
            try
            {
                var result = await _auth.VerifyEmailAsync(Email.Trim(), Code, _pendingToken);
                if (!result.Success)
                {
                    // The ticket outlived its 30 minutes: only a fresh sign-in issues another.
                    if (result.ErrorCode == ErrorCodes.InvalidTicket)
                    {
                        _pendingToken = null;
                        GoLogin();
                    }
                    // Already confirmed elsewhere — the password is all that is needed now.
                    else if (result.ErrorCode == ErrorCodes.AlreadyVerified)
                        GoLogin();

                    ShowError(result.Message);
                    Code = string.Empty;
                    return;
                }
                _pendingToken = null;
                Password = string.Empty;
                _nav.Reset(AppScreen.Home);
            }
            catch (Exception ex)
            {
                ShowError($"Ошибка сети: {ex.Message}");
            }
            finally { IsBusy = false; }
        }

        [RelayCommand]
        private async Task ResendCodeAsync()
        {
            if (IsBusy) return;
            ClearError();

            IsBusy = true;
            try
            {
                var result = await _auth.ResendCodeAsync(Email.Trim(), _pendingToken);
                if (!result.Success)
                {
                    if (result.ErrorCode == ErrorCodes.InvalidTicket)
                    {
                        _pendingToken = null;
                        GoLogin();
                    }
                    ShowError(result.Message ?? "Не удалось отправить код.");
                    return;
                }

                CodeExpiresInSeconds = result.CodeExpiresInSeconds;
                await Dialog.Alert("Код отправлен", $"Мы повторно отправили код на {EmailShown}.");
            }
            catch (Exception ex)
            {
                ShowError($"Ошибка сети: {ex.Message}");
            }
            finally { IsBusy = false; }
        }

        // ── Reset password → mails a reset link ──
        [RelayCommand]
        private async Task DoResetAsync()
        {
            if (!CanReset || IsBusy) return;
            ClearError();

            IsBusy = true;
            try
            {
                var result = await _auth.RequestPasswordResetAsync(Email.Trim());
                if (!result.Success)
                {
                    ShowError(result.Message ?? "Не удалось отправить письмо.");
                    return;
                }

                // The endpoint answers 202 regardless of whether the address exists, so
                // the confirmation intentionally reveals nothing about the account.
                ResetSent = true;
            }
            catch (Exception ex)
            {
                ShowError($"Ошибка сети: {ex.Message}");
            }
            finally { IsBusy = false; }
        }

        /// <summary>Wipes fields — called on logout so the next login starts clean.</summary>
        public void Reset()
        {
            Email = Username = Password = Code = string.Empty;
            _pendingToken = null;
            _emailMasked = string.Empty;
            _confirmFromLogin = false;
            ResetSent = false;
            CodeExpiresInSeconds = 0;
            ClearError();
        }

        private void ShowError(string? message)
        {
            ErrorMessage = string.IsNullOrWhiteSpace(message) ? "Что-то пошло не так." : message;
            HasError = true;
        }

        private void ClearError() { ErrorMessage = string.Empty; HasError = false; }
    }
}
