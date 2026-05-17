using PrintBit.Infrastructure.IPC;

namespace PrintBit.HardwareService.Services;

public class NamedPipeHostedService : BackgroundService
{
    private readonly INamedPipeServer _pipeServer;

    public NamedPipeHostedService(
        INamedPipeServer pipeServer)
    {
        _pipeServer = pipeServer;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        await _pipeServer.StartAsync(
            stoppingToken);
    }
}