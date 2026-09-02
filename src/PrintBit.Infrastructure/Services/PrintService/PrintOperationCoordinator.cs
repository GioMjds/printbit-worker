using System;
using System.Threading;
using System.Threading.Tasks;

namespace PrintBit.Infrastructure.Services.PrintService;

public sealed class PrintOperationCoordinator : IPrinterOperationCoordinator, IDisposable
{
    private readonly SemaphoreSlim _operationLease = new(1, 1);
    private int _disposed;

    public async Task<IDisposable> AcquirePrintAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationLease.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new OperationLease(_operationLease);
    }

    public bool TryAcquireRecovery(out IDisposable? lease)
    {
        ThrowIfDisposed();

        if (!_operationLease.Wait(0))
        {
            lease = null;
            return false;
        }

        lease = new OperationLease(_operationLease);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _operationLease.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private sealed class OperationLease : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public OperationLease(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}
