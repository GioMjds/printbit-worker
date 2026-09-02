namespace PrintBit.Infrastructure.IPC;

public interface IWorkerEventPipeClient
{
    Task<bool> SendAsync(
        WorkerPrintEvent evt,
        CancellationToken cancellationToken = default);

    Task<bool> PublishAsync(
        WorkerPrintEvent evt,
        CancellationToken cancellationToken = default) => SendAsync(evt, cancellationToken);
}
