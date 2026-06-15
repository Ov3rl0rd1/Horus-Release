using Horus.Presentation.ViewModels;

namespace Horus.Presentation.View
{
    public partial class RegisterPage : ContentPage
    {
        private readonly RegisterViewModel _vm;

        public RegisterPage(RegisterViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            BindingContext = vm;
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            this.Opacity = 0;
            this.TranslationY = 30;
            this.FadeTo(1, 350);
            this.TranslateTo(0, 0, 350, Easing.CubicOut);
        }
    }
}
