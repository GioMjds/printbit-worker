using Microsoft.Extensions.Logging;
using PrintBit.Application.Events;
using PrintBit.Application.Handlers;
using PrintBit.Application.StateMachine;
using PrintBit.Hardware.Devices.ESP32;
using PrintBit.Shared.Enums;

namespace PrintBit.Application.Services;

public class HardwareOrchestrator
{
    private readonly ILogger<HardwareOrchestrator> _logger;

    private readonly CoinInsertedHandler _coinHandler;

    private readonly StartPrintHandler _printHandler;

    private readonly TransactionStateMachine _stateMachine;

    public HardwareOrchestrator(
        ILogger<HardwareOrchestrator> logger,
        CoinInsertedHandler coinHandler,
        StartPrintHandler printHandler,
        TransactionStateMachine stateMachine)
    {
        _logger = logger;

        _coinHandler = coinHandler;

        _printHandler = printHandler;

        _stateMachine = stateMachine;
    }

    public async Task HandleEsp32MessageAsync(
        Esp32Message message)
    {
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

                break;
        }

        if (_stateMachine.CurrentState ==
            TransactionState.ReadyToPrint)
        {
            await _printHandler.HandleAsync(
                new StartPrintEvent
                {
                    FilePath = @"C:\PrintBit\sample.pdf"
                });
        }
    }
}