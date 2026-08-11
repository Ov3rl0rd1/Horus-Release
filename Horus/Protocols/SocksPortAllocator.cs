using System.Net;
using System.Net.Sockets;
using Horus.Domain.Models;

namespace Horus.Protocols
{
    /// <summary>
    /// Picks the loopback port that xray's SOCKS5 inbound will listen on and the TUN bridge
    /// will dial.
    ///
    /// 1080 is the conventional choice and stays the first preference, but it is only a
    /// convention: on desktop it is routinely already taken by another proxy client, and a
    /// fixed port turns that into a failed connect for a reason the user cannot act on.
    /// Both ends of the contract are generated from whatever this returns, so moving the
    /// port is safe as long as it is chosen once per connect and threaded through
    /// <see cref="TunnelOptions.SocksPort"/>.
    /// </summary>
    public static class SocksPortAllocator
    {
        /// <summary>How far past the preferred port to look before giving up.</summary>
        private const int Span = 20;

        public static int Allocate(int preferred = XrayConfig.DefaultSocksPort)
        {
            for (var port = preferred; port < preferred + Span; port++)
                if (IsFree(port))
                    return port;

            throw new InvalidOperationException(
                $"Не удалось найти свободный локальный порт в диапазоне " +
                $"{preferred}–{preferred + Span - 1}. Закройте другие прокси-клиенты.");
        }

        /// <summary>
        /// Free means "we can bind it right now". Enumerating listeners would miss a socket
        /// bound to a specific address, and there is no way to hold the reservation anyway —
        /// the core binds it milliseconds later, and a loser gets a clear start failure.
        /// </summary>
        private static bool IsFree(int port)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }
    }
}
