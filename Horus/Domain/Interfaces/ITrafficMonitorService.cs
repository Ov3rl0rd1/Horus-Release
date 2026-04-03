using Horus.Domain.Events;
using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    public interface ITrafficMonitorService
    {
        TrafficStats CurrentStats { get; }

        void Start();
        void Stop();
        void Reset();

        event EventHandler<TrafficUpdatedEventArgs> TrafficUpdated;   // каждую секунду
    }
}
