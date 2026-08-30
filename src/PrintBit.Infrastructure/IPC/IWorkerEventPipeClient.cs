namespace PrintBit.Infrastructure.IPC;

public interface IWorkerEventPipeClient
{
    Task<bool> SendAsync(
        WorkerPrintEvent evt,
        CancellationToken cancellationToken = default);
}
