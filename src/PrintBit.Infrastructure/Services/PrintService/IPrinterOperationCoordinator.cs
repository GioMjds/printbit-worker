using System;
using System.Threading;
using System.Threading.Tasks;

namespace PrintBit.Infrastructure.Services.PrintService;

public interface IPrinterOperationCoordinator
{
    Task<IDisposable> AcquirePrintAsync(CancellationToken cancellationToken);
    bool TryAcquireRecovery(out IDisposable? lease);
}
