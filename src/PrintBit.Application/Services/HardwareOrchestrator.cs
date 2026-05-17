using Microsoft.Extensions.Logging;
using PrintBit.Application.Events;
using PrintBit.Application.Handlers;
using PrintBit.Application.StateMachine;
using PrintBit.Hardware.Devices.ESP32;
using PrintBit.Infrastructure.IPC;
using PrintBit.Shared.Enums;

namespace PrintBit.Application.Services;

public class HardwareOrchestrator
{
    private readonly ILogger<HardwareOrchestrator> _logger;

    private readonly CoinInsertedHandler _coinHandler;

    private readonly StartPrintHandler _printHandler;

    private readonly TransactionStateMachine _stateMachine;

    private readonly INamedPipeServer _pipeServer;

    public HardwareOrchestrator(
        ILogger<HardwareOrchestrator> logger,
        CoinInsertedHandler coinHandler,
        StartPrintHandler printHandler,
        TransactionStateMachine stateMachine,
        INamedPipeServer pipeServer)
    {
        _logger = logger;

        _coinHandler = coinHandler;

        _printHandler = printHandler;

        _stateMachine = stateMachine;

        _pipeServer = pipeServer;
    }

    public async Task HandleEsp32MessageAsync(
        Esp32Message message)
    {
        var shouldStartPrint = false;

        _logger.LogInformation(
            "Orchestrator received: {type}",
            message.Type);

        switch (message.Type)
        {
            case Esp32MessageType.CoinInserted:

                _coinHandler.Handle(
                    new CoinInsertedEvent
                    {
                        Amount = message.Value ?? 0
                    });

                await _pipeServer.BroadcastAsync(
                    new PipeMessage
                    {
                        Type = PipeMessageType.CoinInserted,
                        Payload = $"{{\"amount\":{message.Value}}}"
                    });
                shouldStartPrint = _stateMachine.CurrentState == TransactionState.ReadyToPrint;

                break;
        }

        if (shouldStartPrint)
        {
            await _printHandler.HandleAsync(
                new StartPrintEvent
                {
                    FilePath = @"C:\PrintBit\sample.pdf"
                });
        }
    }
}