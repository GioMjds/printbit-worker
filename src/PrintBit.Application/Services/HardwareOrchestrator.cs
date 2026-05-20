using System.Text.Json;
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
                        Payload = JsonSerializer.Serialize(
                            new
                            {
                                amount = message.Value
                            })
                    });

                if (_stateMachine.CurrentState == TransactionState.ReadyToPrint)
                {
                    await HandlePrintRequestAsync(
                        new StartPrintEvent
                        {
                            FilePath = @"C:\PrintBit\sample.pdf"
                        },
                        source: "esp32");
                }

                break;
            case Esp32MessageType.Heartbeat:
                await _pipeServer.BroadcastAsync(
                    new PipeMessage
                    {
                        Type = PipeMessageType.HardwareStatus,
                        Payload = JsonSerializer.Serialize(
                            new
                            {
                                heartbeat = true
                            })
                    });
                break;
            default:
                _logger.LogDebug(
                    "No orchestrator action configured for message type: {type}",
                    message.Type);
                break;
        }
    }

    public async Task<bool> HandlePrintRequestAsync(
        StartPrintEvent request,
        string source,
        CancellationToken cancellationToken = default)
    {
        if (_stateMachine.CurrentState != TransactionState.ReadyToPrint)
        {
            _logger.LogWarning(
                "Rejected print request | Source={source} | State={state}",
                source,
                _stateMachine.CurrentState);

            await _pipeServer.BroadcastAsync(
                new PipeMessage
                {
                    Type = PipeMessageType.Error,
                    Payload = JsonSerializer.Serialize(
                        new
                        {
                            code = "PRINT_REJECTED",
                            source,
                            state = _stateMachine.CurrentState.ToString()
                        })
                },
                cancellationToken);

            return false;
        }

        await _pipeServer.BroadcastAsync(
            new PipeMessage
            {
                Type = PipeMessageType.PrintStarted,
                Payload = JsonSerializer.Serialize(
                    new
                    {
                        source,
                        file = request.FilePath
                    })
            },
            cancellationToken);

        await _printHandler.HandleAsync(
            request,
            cancellationToken);

        var statusType =
            _stateMachine.CurrentState == TransactionState.Success
                ? PipeMessageType.PrintCompleted
                : PipeMessageType.Error;

        var payload = statusType == PipeMessageType.PrintCompleted
            ? JsonSerializer.Serialize(
                new
                {
                    source,
                    state = _stateMachine.CurrentState.ToString()
                })
            : JsonSerializer.Serialize(
                new
                {
                    code = "PRINT_FAILED",
                    source,
                    state = _stateMachine.CurrentState.ToString(),
                    reason = _stateMachine.LastFailureReason
                });

        await _pipeServer.BroadcastAsync(
            new PipeMessage
            {
                Type = statusType,
                Payload = payload
            },
            cancellationToken);

        return true;
    }

    public async Task HandlePipeMessageAsync(
        PipeMessage message,
        CancellationToken cancellationToken = default)
    {
        if (message.Type != PipeMessageType.ResetTransactionRequest)
        {
            _logger.LogDebug(
                "Ignoring unsupported pipe command: {type}",
                message.Type);

            return;
        }

        _logger.LogInformation(
            "Reset transaction requested by named pipe client");

        _stateMachine.Reset();

        await _pipeServer.BroadcastAsync(
            new PipeMessage
            {
                Type = PipeMessageType.TransactionStatus,
                Payload = JsonSerializer.Serialize(
                    new
                    {
                        state = "Idle",
                        balance = 0
                    })
            },
            cancellationToken);
    }
}
