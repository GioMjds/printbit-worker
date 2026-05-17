namespace PrintBit.Infrastructure.IPC;

public interface INamedPipeServer
{
    Task StartAsync(
        CancellationToken cancellationToken = default);

    Task BroadcastAsync(
        PipeMessage message,
        CancellationToken cancellationToken = default);
}