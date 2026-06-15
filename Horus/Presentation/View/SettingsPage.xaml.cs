using Horus.Domain.Models;
using Horus.Presentation.ViewModels;

namespace Horus.Presentation.View
{
    public partial class SettingsPage : ContentPage
    {
        private readonly SettingsViewModel _vm;

        public SettingsPage(SettingsViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            BindingContext = vm;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await _vm.InitializeAsync();

            // Sync mode picker to current setting
            SplitModePicker.SelectedIndex = (int)_vm.SplitTunnelingMode;
        }

        private void OnSplitModeChanged(object? sender, EventArgs e)
        {
            if (sender is Picker picker)
                _vm.SplitTunnelingMode = (SplitTunnelingMode)picker.SelectedIndex;
        }

        private async void OnLoadAppsClicked(object? sender, EventArgs e)
        {
            await _vm.LoadAppsCommand.ExecuteAsync(null);
        }

        private async void OnAppCheckChanged(object? sender, CheckedChangedEventArgs e)
        {
            if (sender is CheckBox cb && cb.BindingContext is AppOrProcessEntry entry)
                await _vm.ToggleAppCommand.ExecuteAsync(entry);
        }
    }
}
