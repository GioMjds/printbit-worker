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
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;

namespace PrintBit.HardwareService.Services;

public sealed class WorkerCommandPipeHostedService : BackgroundService
{
    private readonly ILogger<WorkerCommandPipeHostedService> _logger;
    private readonly IPrinterRecoveryService _recoveryService;
    private readonly IpcSettings _settings;

    public WorkerCommandPipeHostedService(
        ILogger<WorkerCommandPipeHostedService> logger,
        IPrinterRecoveryService recoveryService,
        IOptions<IpcSettings> options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _recoveryService = recoveryService ?? throw new ArgumentNullException(nameof(recoveryService));
        _settings = options?.Value ?? new IpcSettings();
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
                        ex,
                        "Worker command pipe client disconnected prematurely on {pipe}",
                        _settings.WorkerCommandPipeName);
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

        var responseJson = JsonSerializer.Serialize(result, WorkerCommandParser.JsonOptions) + "\n";
        var responseBytes = Encoding.UTF8.GetBytes(responseJson);

        await outputStream.WriteAsync(responseBytes, cancellationToken);
        await outputStream.FlushAsync(cancellationToken);

        return result;
    }
}
