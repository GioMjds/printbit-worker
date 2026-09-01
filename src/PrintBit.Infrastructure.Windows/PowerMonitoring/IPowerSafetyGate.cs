using System;

namespace PrintBit.Infrastructure.Windows.PowerMonitoring;

public interface IPowerSafetyGate
{
    IPowerDispatchLease? TryAcquirePrintLease();
    bool IsDispatchAllowed { get; }
}

public interface IPowerDispatchLease : IDisposable
{
}
