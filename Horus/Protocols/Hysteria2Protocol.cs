using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Protocols
{
    public class Hysteria2Protocol : IVpnProtocol
    {
        private readonly IProcessRunner _runner;    // запуск бинарника
        private readonly IFileSystem _fs;
        //private readonly ILogger _logger;
        //private Process? _process;

        public ProtocolType Type => ProtocolType.Hysteria2;

        public ProtocolConfig Config => new Hysteria2Config();

        private partial Task WriteConfigFileAsync(Hysteria2Config cfg);   // ~/hysteria2_config.json
        private partial Task WaitForReadyAsync(CancellationToken ct);     // парсит stdout "started"
        private partial void SubscribeToStdout();                          // -> StatisticsUpdated
        public IReadOnlyList<ProtocolParam> GetEditableParams() =>
            [ new ProtocolParam() { Key="hopInterval", Label="UDP Port Hopping", ParamType=ParamType.Bool, DefaultValue=false },
        ];

        public Task ConnectAsync(ProtocolConfig config, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task DisconnectAsync()
        {
            throw new NotImplementedException();
        }

        public Task<bool> ValidateConfigAsync(ProtocolConfig config)
        {
            throw new NotImplementedException();
        }

        public void ApplyParams(IDictionary<string, object> values)
        {
            throw new NotImplementedException();
        }

        public event EventHandler<VpnStatusChangedEventArgs> StatusChanged;
        public event EventHandler<TrafficStatisticsEventArgs> StatisticsUpdated;
        public event EventHandler<ProtocolErrorEventArgs> ErrorOccurred;
    }
}
