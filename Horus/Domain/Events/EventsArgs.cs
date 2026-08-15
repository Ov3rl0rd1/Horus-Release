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
    public record AuthStateChangedEventArgs(bool IsAuthenticated, User? User);
    public record SubscriptionChangedEventArgs(SubscriptionInfo? Info, bool IsExpired);
    public record GeoDataUpdatedEventArgs(string Type, DateTime UpdatedAt);
    public record ServerChangedEventArgs(ServerInfo? Previous, ServerInfo Current);
    public record ProtocolFallbackEventArgs(string FromProtocol, string ToProtocol, string Reason);
    public record BinaryUpdateEventArgs(string BinaryName, string FromVersion, string ToVersion);
    public record ErrorReportSentEventArgs(bool Success, string? FailureReason);

    /// <summary>
    /// The physical link changed. <paramref name="IsHandover"/> distinguishes swapping one
    /// working network for another — Wi-Fi to mobile, the case that needs an immediate
    /// re-check — from simply going offline, where the only correct action is to wait.
    /// </summary>
    public record NetworkChangedEventArgs(NetworkTransport Transport, bool IsOnline, bool IsHandover);

    public record TunnelHealthEventArgs(TunnelHealth Health, string Detail);
}
