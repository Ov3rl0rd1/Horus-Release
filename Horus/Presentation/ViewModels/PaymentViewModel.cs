using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Horus.Presentation.ViewModels
{
    /// <summary>
    /// Subscription / payment overlay (singleton so Home's renew banner and the
    /// sidebar's renew button both drive the same modal).
    ///
    /// <b>Placeholder</b>: there is no billing backend yet. Plans/prices are static,
    /// and "Оплатить" simulates success after a short delay. See
    /// PLAN-remaining-functions.md for the real integration.
    /// </summary>
    public partial class PaymentViewModel : ObservableObject
    {
        public PaymentViewModel()
        {
            Plans.Add(new PlanOption(1, "1 месяц", 199, "без скидки", null));
            Plans.Add(new PlanOption(2, "2 месяца", 378, "≈ 189 ₽/мес", null));
            Plans.Add(new PlanOption(3, "3 месяца", 499, "≈ 166 ₽/мес", "−16%"));
            SelectPlan(Plans[2]);
        }

        // Overlay visibility + step (plans → form → done)
        [ObservableProperty] private bool _isVisible;
        [ObservableProperty] private bool _isPlansStep = true;
        [ObservableProperty] private bool _isFormStep;
        [ObservableProperty] private bool _isDoneStep;

        [ObservableProperty] private bool _isPaying;
        [ObservableProperty] private bool _payByCard = true;
        [ObservableProperty] private bool _payBySbp;

        [ObservableProperty] private PlanOption? _selectedPlan;

        public ObservableCollection<PlanOption> Plans { get; } = new();

        public string PriceString => SelectedPlan is null ? "" : $"{SelectedPlan.Price} ₽";
        public string ContinueLabel => $"Продолжить — {PriceString}";
        public string PayLabel => IsPaying ? "Оплата…" : $"Оплатить {PriceString}";
        public string PlanWord => SelectedPlan?.Title ?? "";
        public string SubUntil => DateTime.Now.AddDays((SelectedPlan?.Months ?? 1) * 30).ToString("d MMMM");

        public void Open()
        {
            IsPlansStep = true; IsFormStep = false; IsDoneStep = false;
            IsPaying = false;
            IsVisible = true;
        }

        [RelayCommand] private void Close() => IsVisible = false;

        [RelayCommand]
        private void SelectPlan(PlanOption? plan)
        {
            if (plan is null) return;
            foreach (var p in Plans) p.IsSelected = p.Months == plan.Months;
            SelectedPlan = plan;
            OnPropertyChanged(nameof(PriceString));
            OnPropertyChanged(nameof(ContinueLabel));
            OnPropertyChanged(nameof(PayLabel));
            OnPropertyChanged(nameof(PlanWord));
            OnPropertyChanged(nameof(SubUntil));
        }

        [RelayCommand] private void GoForm() { IsPlansStep = false; IsFormStep = true; IsDoneStep = false; }
        [RelayCommand] private void BackToPlans() { IsPlansStep = true; IsFormStep = false; IsDoneStep = false; }

        [RelayCommand] private void UseCard() { PayByCard = true; PayBySbp = false; }
        [RelayCommand] private void UseSbp() { PayByCard = false; PayBySbp = true; }

        /// <summary>
        /// Payment is not implemented. This deliberately does <b>not</b> fake success:
        /// showing "Оплачено" while granting nothing left the user bouncing between the
        /// connect gate and this sheet forever, and taught testers the app lies about
        /// state. Access is granted manually until billing exists.
        /// </summary>
        [RelayCommand]
        private async Task PayAsync()
        {
            await Navigation.Dialog.Alert(
                "Оплата пока недоступна",
                $"Приём платежей ещё не подключён. Напишите {AppConfiguration.SupportHandle}, " +
                "чтобы получить доступ.");
        }
    }

    public partial class PlanOption : ObservableObject
    {
        [ObservableProperty] private bool _isSelected;

        public int Months { get; }
        public string Title { get; }
        public int Price { get; }
        public string Per { get; }
        public string? Badge { get; }
        public bool HasBadge => !string.IsNullOrEmpty(Badge);
        public string PriceText => $"{Price} ₽";

        public PlanOption(int months, string title, int price, string per, string? badge)
        {
            Months = months; Title = title; Price = price; Per = per; Badge = badge;
        }
    }
}
