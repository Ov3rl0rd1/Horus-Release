namespace Horus.Domain.Models
{
    public enum VpnState 
    { 
        Disconnected, 
        Connecting, 
        Connected, 
        Disconnecting, 
        Reconnecting, 
        Error 
    }
}
