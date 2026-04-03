using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application
{
    public class TrafficMonitorService : ITrafficMonitorService
    {
        public TrafficStats CurrentStats => throw new NotImplementedException();

        public event EventHandler<TrafficUpdatedEventArgs> TrafficUpdated;

        public void Reset()
        {
            throw new NotImplementedException();
        }

        public void Start()
        {
            throw new NotImplementedException();
        }

        public void Stop()
        {
            throw new NotImplementedException();
        }
    }
}
