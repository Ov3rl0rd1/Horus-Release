using Horus.Application;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using Horus.Presentation.Navigation;

namespace Horus.Presentation.ViewModels
{
    /// <summary>
    /// Moves the account to the server the user picked, once they have agreed to what it
    /// costs: the server is the account's, so every other device on it loses its connection
    /// until it follows.
    /// </summary>
    public sealed class ServerSwitcher
    {
        private readonly IAccountServer _accountServer;

        public ServerSwitcher(IAccountServer accountServer) => _accountServer = accountServer;

        public async Task SwitchToAsync(ServerInfo server)
        {
            if (!AccountServerRules.IsChange(server.Id, _accountServer.Current?.Id)) return;

            var agreed = await Dialog.Confirm(
                "Сменить сервер?",
                $"Аккаунт перейдёт на сервер «{server.Location}». Сервер один на все устройства " +
                "аккаунта: подключения на других устройствах могут оборваться, пока они не перейдут на новый.",
                "Сменить", "Отмена");
            if (!agreed) return;

            try
            {
                await _accountServer.SelectAsync(server);
            }
            catch (SubscriptionExpiredException)
            {
                await SubscriptionPage.OfferAsync();
            }
            catch (UnauthorizedAccessException)
            {
                // The session is gone; the shell is already on its way to the sign-in screen.
            }
            catch (Exception ex)
            {
                await Dialog.Alert("Не удалось сменить сервер", ex.Message);
            }
        }
    }
}
