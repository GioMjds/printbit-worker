using System.Collections.Concurrent;

namespace PrintBit.Infrastructure.Services.PrintService;

public sealed class PrintHealthCoordinator : IPrintHealthCoordinator
{
    private readonly ConcurrentDictionary<string, AttemptState> _activeAttempts = new(StringComparer.OrdinalIgnoreCase);

    public IDisposable BeginAttempt(
        string printerName,
        string expectedDocument)
    {
        var attempt = new AttemptState(
            Guid.NewGuid(),
            expectedDocument);

        _activeAttempts.AddOrUpdate(
            printerName,
            attempt,
            (_, _) => attempt);

        return new AttemptLease(
            this,
            printerName,
            attempt.AttemptId);
    }

    public void ReportFatalHardwareError(
        string printerName,
        int errorCode,
        string message)
    {
        if (!_activeAttempts.TryGetValue(
                printerName,
                out var attempt))
        {
            return;
        }

        lock (attempt.SyncRoot)
        {
            attempt.FatalError ??= new HardwareErrorSignal(
                errorCode,
                message,
                DateTime.UtcNow);
        }
    }

    public bool TryGetFatalHardwareError(
        string printerName,
        out HardwareErrorSignal signal)
    {
        signal = null!;

        if (!_activeAttempts.TryGetValue(
                printerName,
                out var attempt))
        {
            return false;
        }

        lock (attempt.SyncRoot)
        {
            if (attempt.FatalError is null)
            {
                return false;
            }

            signal = attempt.FatalError;
            return true;
        }
    }

    private void EndAttempt(
        string printerName,
        Guid attemptId)
    {
        if (!_activeAttempts.TryGetValue(
                printerName,
                out var currentAttempt))
        {
            return;
        }

        if (currentAttempt.AttemptId == attemptId)
        {
            _activeAttempts.TryRemove(printerName, out _);
        }
    }

    private sealed class AttemptState
    {
        public AttemptState(
            Guid attemptId,
            string expectedDocument)
        {
            AttemptId = attemptId;
            ExpectedDocument = expectedDocument;
        }

        public Guid AttemptId { get; }

        public string ExpectedDocument { get; }

        public object SyncRoot { get; } = new();

        public HardwareErrorSignal? FatalError { get; set; }
    }

    private sealed class AttemptLease : IDisposable
    {
        private readonly PrintHealthCoordinator _owner;
        private readonly string _printerName;
        private readonly Guid _attemptId;
        private bool _disposed;

        public AttemptLease(
            PrintHealthCoordinator owner,
            string printerName,
            Guid attemptId)
        {
            _owner = owner;
            _printerName = printerName;
            _attemptId = attemptId;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _owner.EndAttempt(
                _printerName,
                _attemptId);
        }
    }
}
