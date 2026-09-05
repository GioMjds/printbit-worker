using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Application.Services;
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;

namespace PrintBit.HardwareService.Services;

public sealed class WorkerCommandPipeHostedService : BackgroundService
{
    private readonly ILogger<WorkerCommandPipeHostedService> _logger;
    private readonly IPrinterRecoveryService _recoveryService;
    private readonly IpcSettings _settings;
    private readonly IHardwareOrchestrator? _hardwareOrchestrator;
    private readonly IWorkerEventPipeClient? _eventPipeClient;
    private readonly bool _enableCoinSimulation;

    public WorkerCommandPipeHostedService(
        ILogger<WorkerCommandPipeHostedService> logger,
        IPrinterRecoveryService recoveryService,
        IOptions<IpcSettings> options,
        IHardwareOrchestrator? hardwareOrchestrator = null,
        IWorkerEventPipeClient? eventPipeClient = null,
        IOptions<HardwareSettings>? hardwareSettings = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _recoveryService = recoveryService ?? throw new ArgumentNullException(nameof(recoveryService));
        _settings = options?.Value ?? new IpcSettings();
        _hardwareOrchestrator = hardwareOrchestrator;
        _eventPipeClient = eventPipeClient;
        _enableCoinSimulation = hardwareSettings?.Value.EnableCoinSimulation ?? false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Worker command pipe listener starting on {pipe}",
            _settings.WorkerCommandPipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var server = WorkerCommandPipeSecurity.CreateServerStream(
                    _settings.WorkerCommandPipeName,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                try
                {
                    await server.WaitForConnectionAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                _logger.LogInformation("Worker command pipe client connected");

                try
                {
                    await ProcessRequestAsync(server, server, stoppingToken);

                    if (server.IsConnected && OperatingSystem.IsWindows())
                    {
                        try
                        {
                            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            drainCts.CancelAfter(TimeSpan.FromSeconds(2));
                            await Task.Run(server.WaitForPipeDrain, drainCts.Token);
                        }
                        catch (Exception)
                        {
                            // Drain timeout or client already disconnected; proceed to disconnect
                        }
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogInformation(
                        "Worker command pipe client disconnected on {pipe}: {message}",
                        _settings.WorkerCommandPipeName,
                        ex.Message);
                }
                finally
                {
                    if (server.IsConnected)
                    {
                        server.Disconnect();
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Worker command pipe at {pipe} is already in use or access was denied. Retrying in 5 seconds...",
                    _settings.WorkerCommandPipeName);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unexpected error in worker command pipe listener on {pipe}. Retrying in 1 second...",
                    _settings.WorkerCommandPipeName);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        _logger.LogInformation(
            "Worker command pipe listener stopped on {pipe}",
            _settings.WorkerCommandPipeName);
    }

    public async Task<PrinterRecoveryResult?> ProcessRequestAsync(
        Stream inputStream,
        Stream outputStream,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        var (line, oversized) = await WorkerCommandParser.ReadLineWithLimitAsync(
            inputStream,
            _settings.MaxMessageBytes,
            cancellationToken);

        PrinterRecoveryResult result;

        if (oversized)
        {
            sw.Stop();
            result = new PrinterRecoveryResult
            {
                RequestId = string.Empty,
                Type = PrinterRecoveryCommandType.GetPrinterRecoveryStatus,
                Outcome = PrinterRecoveryOutcome.InvalidRequest,
                Action = null,
                SpoolerState = null,
                PrinterState = null,
                IssueKind = null,
                Message = $"Request payload exceeded maximum allowed size of {_settings.MaxMessageBytes} bytes.",
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow
            };

            _logger.LogWarning(
                "Rejected oversized worker command | Outcome={outcome} ElapsedMs={elapsedMs}",
                result.Outcome,
                sw.ElapsedMilliseconds);
        }
        else if (line is null)
        {
            // Empty stream / EOF without data
            return null;
        }
        else
        {
            string? peekType = null;
            try
            {
                using var peekDoc = JsonDocument.Parse(line);
                if (peekDoc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in peekDoc.RootElement.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, "type", StringComparison.OrdinalIgnoreCase) &&
                            prop.Value.ValueKind == JsonValueKind.String)
                        {
                            peekType = prop.Value.GetString();
                            break;
                        }
                    }
                }
            }
            catch
            {
                // Malformed JSON -> will be handled by WorkerCommandParser.TryParse below
            }

        if (WorkerCommandParser.IsHardwareCommandType(peekType))
        {
            if (!WorkerCommandParser.TryParseHardwareCommand(
                line,
                _settings.MaxMessageBytes,
                out var hwCommand,
                out var hwError,
                out var hwRequestId,
                out var hwCommandType))
            {
                sw.Stop();
                var errorResponse = new HardwareErrorResponse
                {
                    RequestId = hwRequestId,
                    Type = hwCommandType ?? peekType,
                    Success = false,
                    ErrorCode = "INVALID_REQUEST",
                    Message = hwError ?? "Invalid hardware command request."
                };

                _logger.LogWarning(
                    "Rejected invalid hardware worker command | RequestId={requestId} Type={type} Message={message} ElapsedMs={elapsedMs}",
                    errorResponse.RequestId,
                    errorResponse.Type,
                    errorResponse.Message,
                    sw.ElapsedMilliseconds);

                var errorJson = JsonSerializer.Serialize(errorResponse, WorkerCommandParser.JsonOptions) + "\n";
                await outputStream.WriteAsync(Encoding.UTF8.GetBytes(errorJson), cancellationToken);
                await outputStream.FlushAsync(cancellationToken);
                return null;
            }

            if (_hardwareOrchestrator == null)
            {
                sw.Stop();
                var unavailableResponse = new HardwareErrorResponse
                {
                    RequestId = hwCommand.RequestId,
                    Type = peekType,
                    Success = false,
                    ErrorCode = "HARDWARE_ORCHESTRATOR_UNAVAILABLE",
                    Message = "Hardware orchestrator is not configured or available."
                };

                _logger.LogWarning(
                    "Rejected hardware worker command (orchestrator unavailable) | RequestId={requestId} Type={type} ElapsedMs={elapsedMs}",
                    unavailableResponse.RequestId,
                    unavailableResponse.Type,
                    sw.ElapsedMilliseconds);

                var unavailJson = JsonSerializer.Serialize(unavailableResponse, WorkerCommandParser.JsonOptions) + "\n";
                await outputStream.WriteAsync(Encoding.UTF8.GetBytes(unavailJson), cancellationToken);
                await outputStream.FlushAsync(cancellationToken);
                return null;
            }

            string hwResponseJson;
            try
            {
                switch (hwCommand)
                {
                    case SimulateCoinCommand simulateCmd:
                        var simulationResponse = await SimulateCoinAsync(simulateCmd, cancellationToken);
                        hwResponseJson = JsonSerializer.Serialize(simulationResponse, WorkerCommandParser.JsonOptions) + "\n";
                        break;

                    case DispenseCoinsCommand dispenseCmd:
                        var dispenseResult = await _hardwareOrchestrator.DispenseCoinsAsync(
                            dispenseCmd.RequestId,
                            dispenseCmd.CoinCount,
                            dispenseCmd.TimeoutMs,
                            cancellationToken);

                        var dispenseResponse = new DispenseCoinsResponse
                        {
                            RequestId = dispenseCmd.RequestId,
                            Type = "DispenseCoins",
                            Success = dispenseResult.Success,
                            DispensedCoins = dispenseResult.DispensedCoins,
                            ErrorCode = dispenseResult.ErrorCode,
                            Message = dispenseResult.Message
                        };
                        hwResponseJson = JsonSerializer.Serialize(dispenseResponse, WorkerCommandParser.JsonOptions) + "\n";
                        break;

                    case LockCoinSlotCommand lockCmd:
                        _hardwareOrchestrator.LockCoinSlot(lockCmd.OwnerId, lockCmd.Reason);
                        var lockResponse = new LockCoinSlotResponse
                        {
                            RequestId = lockCmd.RequestId,
                            Type = "LockCoinSlot",
                            Success = true
                        };
                        hwResponseJson = JsonSerializer.Serialize(lockResponse, WorkerCommandParser.JsonOptions) + "\n";
                        break;

                    case UnlockCoinSlotCommand unlockCmd:
                        var unlocked = _hardwareOrchestrator.UnlockCoinSlot(unlockCmd.OwnerId);
                        var unlockResponse = new UnlockCoinSlotResponse
                        {
                            RequestId = unlockCmd.RequestId,
                            Type = "UnlockCoinSlot",
                            Success = true,
                            Unlocked = unlocked
                        };
                        hwResponseJson = JsonSerializer.Serialize(unlockResponse, WorkerCommandParser.JsonOptions) + "\n";
                        break;

                    case AnnounceKioskIpCommand announceCmd:
                        _hardwareOrchestrator?.AnnounceKioskIp(announceCmd.Ip, announceCmd.Port, announceCmd.Path);
                        var announceResponse = new AnnounceKioskIpResponse
                        {
                            RequestId = announceCmd.RequestId,
                            Type = "AnnounceKioskIp",
                            Success = true
                        };
                        hwResponseJson = JsonSerializer.Serialize(announceResponse, WorkerCommandParser.JsonOptions) + "\n";
                        break;

                    default:
                        var fallbackResponse = new HardwareErrorResponse
                        {
                            RequestId = hwCommand.RequestId,
                            Type = peekType,
                            Success = false,
                            ErrorCode = "UNSUPPORTED_COMMAND",
                            Message = $"Unsupported hardware command: {peekType}"
                        };
                        hwResponseJson = JsonSerializer.Serialize(fallbackResponse, WorkerCommandParser.JsonOptions) + "\n";
                        break;
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(
                    ex,
                    "Unexpected error executing hardware worker command | RequestId={requestId} Type={type} ElapsedMs={elapsedMs}",
                    hwCommand.RequestId,
                    peekType,
                    sw.ElapsedMilliseconds);

                var errorResponse = new HardwareErrorResponse
                {
                    RequestId = hwCommand.RequestId,
                    Type = peekType,
                    Success = false,
                    ErrorCode = "COMMAND_EXECUTION_ERROR",
                    Message = ex.Message
                };

                var errorJson = JsonSerializer.Serialize(errorResponse, WorkerCommandParser.JsonOptions) + "\n";
                await outputStream.WriteAsync(Encoding.UTF8.GetBytes(errorJson), cancellationToken);
                await outputStream.FlushAsync(cancellationToken);
                return null;
            }

            sw.Stop();
            _logger.LogInformation(
                "Executed hardware worker command | Type={type} RequestId={requestId} ElapsedMs={elapsedMs}",
                peekType,
                hwCommand.RequestId,
                sw.ElapsedMilliseconds);

            try
            {
                var hwResponseBytes = Encoding.UTF8.GetBytes(hwResponseJson);
                await outputStream.WriteAsync(hwResponseBytes, cancellationToken);
                await outputStream.FlushAsync(cancellationToken);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(
                    "Worker command pipe client disconnected before hardware response could be delivered | Type={type} RequestId={requestId}: {message}",
                    peekType,
                    hwCommand.RequestId,
                    ex.Message);
            }
            return null;
        }
        else if (!WorkerCommandParser.TryParse(
            line,
            _settings.MaxMessageBytes,
            out var command,
            out var errorDetail,
            out var requestId))
        {
            sw.Stop();
            result = new PrinterRecoveryResult
            {
                RequestId = requestId,
                Type = PrinterRecoveryCommandType.GetPrinterRecoveryStatus,
                Outcome = PrinterRecoveryOutcome.InvalidRequest,
                Action = null,
                SpoolerState = null,
                PrinterState = null,
                IssueKind = null,
                Message = errorDetail ?? "Invalid command request.",
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow
            };

            _logger.LogWarning(
                "Rejected invalid worker command | RequestId={requestId} Outcome={outcome} Message={message} ElapsedMs={elapsedMs}",
                result.RequestId,
                result.Outcome,
                result.Message,
                sw.ElapsedMilliseconds);
        }
        else
        {
            // Valid command -> Dispatch to recovery service
            PrinterRecoveryResult serviceResult;

            if (command.Type == PrinterRecoveryCommandType.GetPrinterRecoveryStatus)
            {
                serviceResult = await _recoveryService.GetStatusAsync(cancellationToken);
            }
            else if (command.Type == PrinterRecoveryCommandType.AttemptPrinterRecovery)
            {
                serviceResult = await _recoveryService.AttemptRepairAsync(cancellationToken);
            }
            else
            {
                serviceResult = new PrinterRecoveryResult
                {
                    RequestId = command.RequestId,
                    Type = command.Type,
                    Outcome = PrinterRecoveryOutcome.InvalidRequest,
                    Action = null,
                    SpoolerState = null,
                    PrinterState = null,
                    IssueKind = null,
                    Message = $"Unsupported command type: {command.Type}",
                    StartedAt = startedAt,
                    CompletedAt = DateTime.UtcNow
                };
            }

            sw.Stop();

            // Ensure RequestId matches incoming command
            result = new PrinterRecoveryResult
            {
                RequestId = command.RequestId,
                Type = serviceResult.Type,
                Outcome = serviceResult.Outcome,
                Action = serviceResult.Action,
                SpoolerState = serviceResult.SpoolerState,
                PrinterState = serviceResult.PrinterState,
                IssueKind = serviceResult.IssueKind,
                Message = serviceResult.Message,
                StartedAt = serviceResult.StartedAt,
                CompletedAt = serviceResult.CompletedAt
            };

            _logger.LogInformation(
                "Executed worker command | Type={type} RequestId={requestId} Outcome={outcome} ElapsedMs={elapsedMs}",
                result.Type,
                result.RequestId,
                result.Outcome,
                sw.ElapsedMilliseconds);
        }
    }

    var responseJson = JsonSerializer.Serialize(result, WorkerCommandParser.JsonOptions) + "\n";
    var responseBytes = Encoding.UTF8.GetBytes(responseJson);

    try
    {
        await outputStream.WriteAsync(responseBytes, cancellationToken);
        await outputStream.FlushAsync(cancellationToken);
    }
    catch (IOException ex)
    {
        _logger.LogDebug(
            "Worker command pipe client disconnected before recovery response could be delivered: {message}",
            ex.Message);
    }

    return result;
}

    private async Task<HardwareCommandResponse> SimulateCoinAsync(
        SimulateCoinCommand command, CancellationToken cancellationToken)
    {
        var response = new HardwareCommandResponse { RequestId = command.RequestId, Type = "SimulateCoin" };
        if (!_enableCoinSimulation)
        {
            return response with { ErrorCode = "SIMULATION_DISABLED",
                Message = "Enable HardwareSettings:EnableCoinSimulation in the worker to use SCC." };
        }

        // Inject a denomination at the hardware-event boundary. No serial device is required.
        // IsCoinSlotLocked includes both power safety and the coin acceptor's session locks.
        var locked = _hardwareOrchestrator?.IsCoinSlotLocked ?? false;
        _logger.LogInformation("[SIMULATED] Coin {Outcome}: {Amount} | RequestId={RequestId}",
            locked ? "rejected (slot_locked)" : "accepted", command.CoinValue, command.RequestId);
        var evt = new WorkerPrintEvent
        {
            Type = locked ? WorkerPrintEventType.CoinRejected : WorkerPrintEventType.CoinInserted,
            RequestId = command.RequestId,
            CoinValue = command.CoinValue,
            Simulated = true,
            RejectReason = locked ? "slot_locked" : null,
            TimestampUtc = DateTime.UtcNow
        };
        var delivered = _eventPipeClient != null && await _eventPipeClient.SendAsync(evt, cancellationToken);
        if (!delivered)
        {
            return response with { ErrorCode = "WORKER_EVENT_UNAVAILABLE",
                Message = "Could not deliver the simulated coin event to Node. Check the return pipe." };
        }
        return response with { Success = !locked, ErrorCode = locked ? "slot_locked" : null,
            Message = locked ? "Coin slot is locked by power safety or an active session." : "Simulated coin event sent to Node." };
    }
}

