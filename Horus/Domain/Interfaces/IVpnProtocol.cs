using Horus.Domain.Events;
using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    public interface IVpnProtocol
    {
        ProtocolType Type { get; }
        ProtocolConfig Config { get; }

        Task ConnectAsync(ProtocolConfig config, CancellationToken ct = default);
        Task DisconnectAsync();
        Task<bool> ValidateConfigAsync(ProtocolConfig config);
        IReadOnlyList<ProtocolParam> GetEditableParams();   // для UI настроек
        void ApplyParams(IDictionary<string, object> values);

        event EventHandler<VpnStatusChangedEventArgs> StatusChanged;
        event EventHandler<TrafficStatisticsEventArgs> StatisticsUpdated;
        event EventHandler<ProtocolErrorEventArgs> ErrorOccurred;
    }
}
