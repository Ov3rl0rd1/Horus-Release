using Horus.Domain.Models;

namespace Horus.Domain.Events
{
    public record VpnStateChangedEventArgs(VpnState OldState, VpnState NewState, string? Reason);
    public record VpnStatusChangedEventArgs(VpnState Status, string? Message);
    public record TunnelStateChangedEventArgs(TunnelState State, string? Error);
    public record TrafficUpdatedEventArgs(TrafficStats Stats);
    public record TrafficStatisticsEventArgs(long UpBps, long DownBps, long TotalUp, long TotalDown);
    public record ProtocolErrorEventArgs(string Code, string Message, bool IsFatal);
    public record ConnectionErrorEventArgs(string Protocol, string Error, bool WillRetry);
    public record AuthStateChangedEventArgs(bool IsAuthenticated, UserInfo? User);
    public record SubscriptionChangedEventArgs(SubscriptionInfo? Info, bool IsExpired);
    public record GeoDataUpdatedEventArgs(string Type, DateTime UpdatedAt);
    public record ServerChangedEventArgs(ServerInfo? Previous, ServerInfo Current);
}
