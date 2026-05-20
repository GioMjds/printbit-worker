using PrintBit.Application.Services;
using PrintBit.Infrastructure.IPC;

namespace PrintBit.HardwareService.Services;

public class NamedPipeHostedService : BackgroundService
{
    private readonly INamedPipeServer _pipeServer;

    private readonly HardwareOrchestrator _orchestrator;

    public NamedPipeHostedService(
        INamedPipeServer pipeServer,
        HardwareOrchestrator orchestrator)
    {
        _pipeServer = pipeServer;
        _orchestrator = orchestrator;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _pipeServer.MessageReceived += OnPipeMessageReceivedAsync;

        await _pipeServer.StartAsync(stoppingToken);
    }

    public override Task StopAsync(
        CancellationToken cancellationToken)
    {
        _pipeServer.MessageReceived -= OnPipeMessageReceivedAsync;

        return base.StopAsync(cancellationToken);
    }

    private Task OnPipeMessageReceivedAsync(
        PipeMessage message,
        CancellationToken cancellationToken)
    {
        return _orchestrator.HandlePipeMessageAsync(
            message,
            cancellationToken);
    }
}
