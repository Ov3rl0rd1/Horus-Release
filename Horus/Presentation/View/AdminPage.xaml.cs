#if ADMIN_MODE
using Horus.Presentation.ViewModels;

namespace Horus.Presentation.View
{
    public partial class AdminPage : ContentPage
    {
        private readonly AdminViewModel _vm;

        // Prevents the Toggled handler from running while InitializeAsync sets IsToggled.
        // The OneWay binding also fires Toggled on programmatic Switch changes;
        // we use the secondary guard (e.Value == _vm.IsLocalMode) for those.
        private bool _suppressModeToggle;

        public AdminPage(AdminViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            BindingContext = vm;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            _suppressModeToggle = true;
            try
            {
                await _vm.InitializeAsync();
            }
            finally
            {
                _suppressModeToggle = false;
            }
        }

        private void OnLocalModeToggled(object? sender, ToggledEventArgs e)
        {
            // Guard 1: suppress during InitializeAsync
            if (_suppressModeToggle) return;

            // Guard 2: binding (Mode=OneWay) propagated a ViewModel-side change to the Switch.
            // At that moment e.Value equals _vm.IsLocalMode — it's a no-op from the user's perspective.
            if (e.Value == _vm.IsLocalMode) return;

            // Genuine user tap: dispatch to command.
            // ToggleLocalModeCommand inverts the mode, calls RefreshLocalModeStatus,
            // which sets IsLocalMode → binding updates Switch → Toggled fires again,
            // but then e.Value == _vm.IsLocalMode → Guard 2 catches it.
            _ = _vm.ToggleLocalModeCommand.ExecuteAsync(null);
        }
    }
}
#else
namespace Horus.Presentation.View
{
    public partial class AdminPage : ContentPage
    {
        public AdminPage()
        {
            throw new InvalidOperationException("Admin mode is not enabled in this build.");
        }
    }
}
#endif
