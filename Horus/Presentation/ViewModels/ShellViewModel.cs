using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Presentation.Navigation;

namespace Horus.Presentation.ViewModels
{
    /// <summary>
    /// Aggregate view-model for the custom <c>RootPage</c>: owns the child screen
    /// view-models, derives which screen is visible from the <see cref="Navigator"/>,
    /// and holds the account/subscription card shown in the Windows sidebar.
    /// </summary>
    public partial class ShellViewModel : ObservableObject
    {
        private readonly Navigator _nav;
        private readonly IAuthService _auth;
        private readonly IApiService _api;

        public Navigator Nav => _nav;
        public MainViewModel Main { get; }
        public ServersViewModel Servers { get; }
        public SettingsViewModel Settings { get; }
        public AuthFlowViewModel Auth { get; }
        public PaymentViewModel Payment { get; }

        public ShellViewModel(
            Navigator nav,
            IAuthService auth,
            IApiService api,
            MainViewModel main,
            ServersViewModel servers,
            SettingsViewModel settings,
            AuthFlowViewModel auth2,
            PaymentViewModel payment)
        {
            _nav = nav;
            _auth = auth;
            _api = api;
            Main = main;
            Servers = servers;
            Settings = settings;
            Auth = auth2;
            Payment = payment;

            _nav.PropertyChanged += OnNavChanged;
            _auth.AuthStateChanged += OnAuthChanged;
            _api.SessionExpired += OnSessionExpired;
        }

        /// <summary>
        /// The API rejected the stored session. Sign out and route to Login — previously
        /// this surfaced as empty server lists and stale account data with no explanation.
        /// </summary>
        private void OnSessionExpired(object? sender, EventArgs e) =>
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (IsAuthFlow) return; // already there

                await _auth.LogoutAsync();
                Auth.Reset();
                _nav.Reset(AppScreen.Login);
                await Dialog.Alert("Сессия истекла", "Пожалуйста, войдите заново.");
            });

        // ── Screen flags (bound to each view's IsVisible) ──
        public AppScreen Screen => _nav.CurrentScreen;
        public bool IsOnboarding => Screen == AppScreen.Onboarding;
        public bool IsLogin => Screen == AppScreen.Login;
        public bool IsRegister => Screen == AppScreen.Register;
        public bool IsConfirm => Screen == AppScreen.Confirm;
        public bool IsReset => Screen == AppScreen.Reset;
        public bool IsHome => Screen == AppScreen.Home;
        public bool IsServers => Screen == AppScreen.Servers;
        public bool IsSettings => Screen == AppScreen.Settings;
        public bool IsSplit => Screen == AppScreen.Split;

        public bool IsAuthFlow => Screen is AppScreen.Onboarding or AppScreen.Login
            or AppScreen.Register or AppScreen.Confirm or AppScreen.Reset;
        public bool InApp => !IsAuthFlow;
        /// <summary>Chrome (tab bar / sidebar) is shown on the three main tabs only.</summary>
        public bool ShowChrome => Screen is AppScreen.Home or AppScreen.Servers
            or AppScreen.Settings or AppScreen.Split;

        // Platform chrome selection (sidebar on desktop, bottom tabs on phone)
        public bool IsDesktop => DeviceInfo.Idiom == DeviceIdiom.Desktop || DeviceInfo.Idiom == DeviceIdiom.Tablet;
        public bool IsPhone => !IsDesktop;
        public bool IsSidebarVisible => IsDesktop && ShowChrome;
        public bool IsBottomBarVisible => IsPhone && ShowChrome;

        // Tab highlight (Settings stays active while on the Split sub-screen)
        public bool TabHomeActive => IsHome;
        public bool TabServersActive => IsServers;
        public bool TabSettingsActive => IsSettings || IsSplit;

        // ── Account / subscription card ──
        public string AccountEmail => _auth.CurrentUser?.username ?? "—";
        private int SubDaysLeft
        {
            get
            {
                var exp = _auth.CurrentUser?.expiresAt;
                if (exp is null) return 0;
                return Math.Max((int)Math.Ceiling((exp.Value.ToUniversalTime() - DateTime.UtcNow).TotalDays), 0);
            }
        }
        public bool HasSubscription => SubDaysLeft > 0;
        public string SubLine => HasSubscription
            ? $"Подписка · до {_auth.CurrentUser!.expiresAt!.Value.ToLocalTime():d MMMM}"
            : "Подписка не активна";
        public Color SubLineColor => HasSubscription ? Color.FromArgb("#F3D48E") : Color.FromArgb("#F2A6A0");
        public string RenewLabel => HasSubscription ? "Продлить подписку" : "Оформить подписку";

        // ── Tab navigation ──
        [RelayCommand] private void NavHome() => _nav.Go(AppScreen.Home);
        [RelayCommand] private void NavServers() => _nav.Go(AppScreen.Servers);
        [RelayCommand] private void NavSettings() => _nav.Go(AppScreen.Settings);
        [RelayCommand] private void OpenPay() => Payment.Open();

        /// <summary>Called from App startup once the session-restore attempt is done.</summary>
        public void Initialize(bool sessionRestored)
        {
            if (sessionRestored)
                _nav.Reset(AppScreen.Home);
            else
                _nav.Reset(DeviceInfo.Idiom == DeviceIdiom.Phone ? AppScreen.Onboarding : AppScreen.Login);
            RaiseAccount();
        }

        /// <summary>Hardware/back-arrow handler. Returns true when it consumed the back.</summary>
        public bool Back()
        {
            if (Payment.IsVisible) { Payment.CloseCommand.Execute(null); return true; }
            return _nav.GoBack();
        }

        private async void OnNavChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(Navigator.CurrentScreen)) return;
            RaiseScreenFlags(); // refresh only the screen/chrome bindings (not the whole VM)

            // Load data lazily — only when it isn't already loaded (avoids re-fetching
            // on every tab switch, which is part of what made switching feel slow).
            if (IsHome)
            {
                if (Main.QuickServers.Count == 0) await Main.LoadQuickServersAsync();
                // Cheap, and the egress IP changes whenever the tunnel does.
                await Main.RefreshAccountAsync();
            }
            else if (IsServers && !Servers.HasLoaded) await Servers.LoadCommand.ExecuteAsync(null);
            else if (IsSettings) await Settings.InitializeAsync();
            else if (IsSplit && Settings.SplitTunnelingSupported && Settings.SplitApps.Count == 0)
                await Settings.LoadAppsCommand.ExecuteAsync(null);
        }

        private void RaiseScreenFlags()
        {
            OnPropertyChanged(nameof(Screen));
            OnPropertyChanged(nameof(IsOnboarding));
            OnPropertyChanged(nameof(IsLogin));
            OnPropertyChanged(nameof(IsRegister));
            OnPropertyChanged(nameof(IsConfirm));
            OnPropertyChanged(nameof(IsReset));
            OnPropertyChanged(nameof(IsHome));
            OnPropertyChanged(nameof(IsServers));
            OnPropertyChanged(nameof(IsSettings));
            OnPropertyChanged(nameof(IsSplit));
            OnPropertyChanged(nameof(IsAuthFlow));
            OnPropertyChanged(nameof(InApp));
            OnPropertyChanged(nameof(ShowChrome));
            OnPropertyChanged(nameof(IsSidebarVisible));
            OnPropertyChanged(nameof(IsBottomBarVisible));
            OnPropertyChanged(nameof(TabHomeActive));
            OnPropertyChanged(nameof(TabServersActive));
            OnPropertyChanged(nameof(TabSettingsActive));
        }

        private void OnAuthChanged(object? sender, AuthStateChangedEventArgs e) =>
            MainThread.BeginInvokeOnMainThread(RaiseAccount);

        private void RaiseAccount()
        {
            OnPropertyChanged(nameof(AccountEmail));
            OnPropertyChanged(nameof(HasSubscription));
            OnPropertyChanged(nameof(SubLine));
            OnPropertyChanged(nameof(SubLineColor));
            OnPropertyChanged(nameof(RenewLabel));
        }
    }
}
