using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
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
        private readonly IAccountSync _accountSync;

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
            IAccountSync accountSync,
            MainViewModel main,
            ServersViewModel servers,
            SettingsViewModel settings,
            AuthFlowViewModel auth2,
            PaymentViewModel payment)
        {
            _nav = nav;
            _auth = auth;
            _api = api;
            _accountSync = accountSync;
            Main = main;
            Servers = servers;
            Settings = settings;
            Auth = auth2;
            Payment = payment;

            _nav.PropertyChanged += OnNavChanged;
            _auth.AuthStateChanged += OnAuthChanged;
            _api.SessionExpired += OnSessionExpired;

            // The sidebar's subscription line is driven by the same poll as the Home
            // banner, so it updates without the user touching anything.
            _accountSync.AccountRefreshed += (_, __) => RaiseAccount();
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
        public bool IsStartup => Screen == AppScreen.Startup;
        public bool IsOnboarding => Screen == AppScreen.Onboarding;
        public bool IsLogin => Screen == AppScreen.Login;
        public bool IsRegister => Screen == AppScreen.Register;
        public bool IsConfirm => Screen == AppScreen.Confirm;
        public bool IsReset => Screen == AppScreen.Reset;
        public bool IsHome => Screen == AppScreen.Home;
        public bool IsServers => Screen == AppScreen.Servers;
        public bool IsSettings => Screen == AppScreen.Settings;
        public bool IsSplit => Screen == AppScreen.Split;

        /// <summary>Startup counts as "outside the app" so no chrome or account card shows.</summary>
        public bool IsAuthFlow => Screen is AppScreen.Startup or AppScreen.Onboarding or AppScreen.Login
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
        /// <summary>True until the server has actually said the subscription is gone.</summary>
        private bool SubscriptionKnown => _auth.SubscriptionState != SubscriptionState.Unknown;

        public bool HasSubscription => SubDaysLeft > 0;

        // While the state is Unknown the card stays neutral rather than announcing
        // "Подписка не активна" on every cold start, before /whoami has answered.
        public string SubLine => _auth.CurrentUser?.expiresAt is { } exp && SubDaysLeft > 0
            ? $"Подписка · до {exp.ToLocalTime():d MMMM}"
            : SubscriptionKnown ? "Подписка не активна" : "Проверяем подписку…";

        public Color SubLineColor => !SubscriptionKnown
            ? Color.FromArgb("#99EFEAF6")
            : HasSubscription ? Color.FromArgb("#F3D48E") : Color.FromArgb("#F2A6A0");

        public string RenewLabel => HasSubscription ? "Продлить подписку" : "Оформить подписку";

        // ── Tab navigation ──
        [RelayCommand] private void NavHome() => _nav.Go(AppScreen.Home);
        [RelayCommand] private void NavServers() => _nav.Go(AppScreen.Servers);
        [RelayCommand] private void NavSettings() => _nav.Go(AppScreen.Settings);
        [RelayCommand] private void OpenPay() => Payment.Open();

        private Task? _startTask;
        private readonly object _startGate = new();

        /// <summary>Set when a required native binary is missing or unusable; blocks startup.</summary>
        [ObservableProperty] private string _startupError = string.Empty;

        public bool HasStartupError => StartupError.Length > 0;

        partial void OnStartupErrorChanged(string value) => OnPropertyChanged(nameof(HasStartupError));

        /// <summary>
        /// Reads the stored session and routes to the first real screen. Called from both
        /// <c>App.OnStart</c> and <c>RootPage.OnAppearing</c> — the app opens on
        /// <see cref="AppScreen.Startup"/>, so if neither hook fired the user would be
        /// stranded there.
        ///
        /// <para>The second caller awaits the first caller's work rather than returning
        /// straight away, and that distinction is load-bearing. A plain "already started"
        /// guard makes the loser return <i>immediately</i>, while the session it is supposed
        /// to have waited for is still being read — and App.OnStart then runs
        /// <c>TryRestoreOrAutoConnectAsync</c> against an unauthenticated manager, which
        /// gives up with "no session" and never tries again. Measured on device: after the
        /// process was killed with the tunnel up, the app came back, the session restored
        /// fine, the UI showed the user logged in — and the VPN stayed off, because the one
        /// path that would have brought it back had already run and lost the race.</para>
        /// </summary>
        public Task EnsureStartedAsync()
        {
            lock (_startGate) return _startTask ??= StartAsync();
        }

        private async Task StartAsync()
        {
            // Before anything else: without the core there is no product, and every later
            // failure would be a confusing symptom of this one. Stay on the startup screen
            // and say exactly which file is missing and where it belongs.
            var deps = Protocols.NativeDependencies.Check();
            if (!deps.IsSatisfied)
            {
                StartupError = Protocols.NativeDependencies.Describe(deps);
                RaiseScreenFlags();
                return;
            }

            bool restored = false;
            try
            {
                // Bounded: a stalled keystore read must not leave the app on a blank screen.
                restored = await _auth.TryRestoreSessionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Horus] Session restore failed: {ex}");
            }

            Initialize(restored);
        }

        /// <summary>Routes to the first screen once the session-restore attempt is done.</summary>
        public void Initialize(bool sessionRestored)
        {

            if (sessionRestored)
                _nav.Reset(AppScreen.Home);
            else
                _nav.Reset(DeviceInfo.Idiom == DeviceIdiom.Phone ? AppScreen.Onboarding : AppScreen.Login);

            RaiseScreenFlags();
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
                // Cheap, and the egress IP changes whenever the tunnel does. Coalesced
                // with whatever the background poll is doing, so a fast tab switch does
                // not stack up requests.
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
            OnPropertyChanged(nameof(IsStartup));
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
