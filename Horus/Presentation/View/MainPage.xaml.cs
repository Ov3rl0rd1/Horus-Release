using Horus.Domain.Models;
using Horus.Presentation.ViewModels;

namespace Horus
{
    public partial class MainPage : ContentPage
    {
        // Подписывается на VpnManager.StateChanged, ITrafficMonitorService.TrafficUpdated

        int count = 0;

        public MainPage(MainViewModel viewModel)
        {
            InitializeComponent();
            this.BindingContext = viewModel;
        }
    }
}
