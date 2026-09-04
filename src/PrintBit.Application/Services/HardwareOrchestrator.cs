using System.Text.Json;
using Microsoft.Extensions.Logging;
using PrintBit.Application.Events;
using PrintBit.Application.Handlers;
using PrintBit.Application.StateMachine;
using PrintBit.Hardware.Devices.CoinAcceptor;
using PrintBit.Hardware.Devices.ESP32;
using PrintBit.Hardware.Devices.Hopper;
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.SerialService;
using PrintBit.Shared.Enums;

namespace PrintBit.Application.Services;

public class HardwareOrchestrator : IHardwareOrchestrator, IDisposable
{
    private readonly ILogger<HardwareOrchestrator> _logger;
    private readonly CoinInsertedHandler _coinHandler;
    private readonly StartPrintHandler _printHandler;
    private readonly TransactionStateMachine _stateMachine;
    private readonly INamedPipeServer _pipeServer;
    private readonly ISerialConnection _serialConnection;
    private readonly IEsp32Device _esp32Device;
    private readonly ICoinAcceptor _coinAcceptor;
    private readonly IHopper _hopper;
    private readonly CoinPulseDecoder _pulseDecoder;
    private readonly IWorkerEventPipeClient? _eventPipeClient;

    private bool _disposed;

    public HardwareOrchestrator(
        ILogger<HardwareOrchestrator> logger,
        CoinInsertedHandler coinHandler,
        StartPrintHandler printHandler,
        TransactionStateMachine stateMachine,
        INamedPipeServer pipeServer,
        ISerialConnection serialConnection,
        IEsp32Device esp32Device,
        ICoinAcceptor coinAcceptor,
        IHopper hopper,
        CoinPulseDecoder pulseDecoder,
        IWorkerEventPipeClient? eventPipeClient = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _coinHandler = coinHandler ?? throw new ArgumentNullException(nameof(coinHandler));
        _printHandler = printHandler ?? throw new ArgumentNullException(nameof(printHandler));
        _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
        _pipeServer = pipeServer ?? throw new ArgumentNullException(nameof(pipeServer));
        _serialConnection = serialConnection ?? throw new ArgumentNullException(nameof(serialConnection));
        _esp32Device = esp32Device ?? throw new ArgumentNullException(nameof(esp32Device));
        _coinAcceptor = coinAcceptor ?? throw new ArgumentNullException(nameof(coinAcceptor));
        _hopper = hopper ?? throw new ArgumentNullException(nameof(hopper));
        _pulseDecoder = pulseDecoder ?? throw new ArgumentNullException(nameof(pulseDecoder));
        _eventPipeClient = eventPipeClient;

        _serialConnection.LineReceived += OnLineReceived;
        _coinAcceptor.CoinAccepted += OnCoinAccepted;
        _coinAcceptor.CoinRejected += OnCoinRejected;
        _hopper.ProgressReceived += OnHopperProgressReceived;
    }

    public bool IsCoinSlotLocked => _coinAcceptor.IsLocked;

    public void LockCoinSlot(string ownerId, string? reason = null)
    {
        _coinAcceptor.Lock(ownerId, reason);
    }

    public bool UnlockCoinSlot(string ownerId)
    {
        return _coinAcceptor.Unlock(ownerId);
    }

    public void ResetCoinSlotLocks()
    {
        _coinAcceptor.ResetLocks();
    }

    public bool IsDispensingCoins => _hopper.IsDispensing;

    public Task<HopperDispenseResult> DispenseCoinsAsync(
        string requestId,
        int coinCount,
        int? timeoutMs = null,
        CancellationToken ct = default)
    {
        return _hopper.DispenseAsync(requestId, coinCount, timeoutMs, ct);
    }

    public void AnnounceKioskIp(string ip, int port, string path)
    {
        _esp32Device.SendKioskIpAnnouncement(ip, port, path);
    }

    public void SendWifiCommand(string action)
    {
        _esp32Device.SendWifiCommand(action);
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
                    await _pipeServer.BroadcastAsync(
                        new PipeMessage
                        {
                            Type = PipeMessageType.TransactionStatus,
                            Payload = JsonSerializer.Serialize(
                                new
                                {
                                    state = _stateMachine.CurrentState.ToString(),
                                    balance = _stateMachine.CurrentBalance
                                })
                        });

                    _logger.LogInformation(
                        "Transaction is ready. Waiting for explicit print confirmation trigger.");
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

    private void OnLineReceived(object? sender, string line)
    {
        if (_disposed || string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            var trimmed = line.Trim();
            if (trimmed == "0" || trimmed == "1" || trimmed == "2" || trimmed == "5")
            {
                _pulseDecoder.ProcessToken(trimmed);
            }
            else if (trimmed.StartsWith("COIN:", StringComparison.OrdinalIgnoreCase))
            {
                var token = trimmed["COIN:".Length..].Trim();
                if (!string.IsNullOrEmpty(token))
                {
                    _pulseDecoder.ProcessToken(token);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing serial line: {Line}", line);
        }
    }

    private void OnCoinAccepted(object? sender, int amount)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _logger.LogInformation("Coin accepted: {Amount}", amount);
            _coinHandler.Handle(new CoinInsertedEvent { Amount = amount });

            _ = _pipeServer.BroadcastAsync(new PipeMessage
            {
                Type = PipeMessageType.CoinInserted,
                Payload = JsonSerializer.Serialize(new { amount })
            });

            if (_stateMachine.CurrentState == TransactionState.ReadyToPrint)
            {
                _ = _pipeServer.BroadcastAsync(new PipeMessage
                {
                    Type = PipeMessageType.TransactionStatus,
                    Payload = JsonSerializer.Serialize(new
                    {
                        state = _stateMachine.CurrentState.ToString(),
                        balance = _stateMachine.CurrentBalance
                    })
                });

                _logger.LogInformation("Transaction is ready. Waiting for explicit print confirmation trigger.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling CoinAccepted event for amount {Amount}", amount);
        }
    }

    private void OnCoinRejected(object? sender, (int Value, string Reason) args)
    {
        if (_disposed)
        {
            return;
        }

        _logger.LogWarning("Coin rejected: Value={Value}, Reason={Reason}", args.Value, args.Reason);
    }

    private void OnHopperProgressReceived(object? sender, (string RequestId, int Dispensed, int Total) args)
    {
        if (_disposed)
        {
            return;
        }

        _logger.LogInformation("Hopper progress for {RequestId}: {Dispensed}/{Total}", args.RequestId, args.Dispensed, args.Total);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _serialConnection.LineReceived -= OnLineReceived;
        _coinAcceptor.CoinAccepted -= OnCoinAccepted;
        _coinAcceptor.CoinRejected -= OnCoinRejected;
        _hopper.ProgressReceived -= OnHopperProgressReceived;

        GC.SuppressFinalize(this);
    }
}
