using System;
using System.Collections.Generic;
using System.Threading;
using SimpleDeFence.Windows.Services;

namespace SimpleDeFence
{
    /// <summary>
    /// The service's per-app traffic counter: a <see cref="KernelNetworkTrace"/> feeding a
    /// <see cref="TrafficLedger"/>, swept every few seconds so a process id Windows reuses is not
    /// charged to the program that had it before. Answers GET_TRAFFIC.
    /// </summary>
    internal sealed class TrafficMonitor : IDisposable
    {
        private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ServiceMapAge = TimeSpan.FromSeconds(30);

        private readonly TrafficLedger _ledger;
        private readonly KernelNetworkTrace? _trace;
        private readonly Timer? _sweepTimer;

        private readonly object _servicesLock = new();
        private ServicePidMap? _services;
        private DateTime _servicesTakenAt;

        public TrafficMonitor()
        {
            _ledger = new TrafficLedger(Utils.GetPathOfProcess, ServicesOf);
            _trace = KernelNetworkTrace.TryStart((pid, bytes, sent) => _ledger.Record(pid, bytes, sent));
            if (_trace is not null)
                _sweepTimer = new Timer(_ => SweepSafely(), null, SweepInterval, SweepInterval);
        }

        /// <summary>False when Windows would not start the trace, so there is nothing to show.
        /// The reason is in the service log.</summary>
        public bool Available => _trace is not null;

        public TwMessageGetTraffic Answer(TwMessageGetTraffic request)
        {
            if (!Available)
                return request.CreateResponse(false, _ledger.Since, Array.Empty<AppTraffic>());

            return request.CreateResponse(true, _ledger.Since, _ledger.Snapshot());
        }

        private void SweepSafely()
        {
            try { _ledger.Sweep(); }
            catch (Exception e) { Utils.LogException(e, Utils.LOG_ID_SERVICE); }
        }

        /// <summary>Listing the services is slow, so one listing is reused for a while. A service
        /// that started since then simply has no name yet; it gets one on a later sweep.</summary>
        private IReadOnlyCollection<string>? ServicesOf(uint pid)
        {
            lock (_servicesLock)
            {
                if (_services is null || DateTime.UtcNow - _servicesTakenAt > ServiceMapAge)
                {
                    _services = ServicePidMap.CreateOrEmpty();
                    _servicesTakenAt = DateTime.UtcNow;
                }
                return _services.GetServicesInPid(pid);
            }
        }

        public void Dispose()
        {
            _sweepTimer?.Dispose();
            _trace?.Dispose();
        }
    }
}
