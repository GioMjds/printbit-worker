using System.Text.Json;
using Microsoft.Extensions.Options;
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;

namespace PrintBit.HardwareService.Services;

public class PrintQueueWatcherService : BackgroundService
{
    // Mirrors PrinterMonitorService: the default 500ms connect
    // timeout is too short when Node.js is still starting alongside
    // the worker, so the first event of a session would be lost.
    private const int NodeConnectTimeoutMs = 3_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HashSet<string> _processingFiles = [];

    private readonly ILogger<PrintQueueWatcherService> _logger;

    private readonly IPrintService _printService;

    private readonly WorkerEventPipeClient _eventPipe;

    private readonly HardwareSettings _settings;

    public PrintQueueWatcherService(
        ILogger<PrintQueueWatcherService> logger,
        IPrintService printService,
        WorkerEventPipeClient eventPipe,
        IOptions<HardwareSettings> options)
    {
        _logger = logger;
        _printService = printService;
        _eventPipe = eventPipe;
        _settings = options.Value;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var queueDirectory = _settings.PrintQueueDirectory;
        var archiveDirectory =
            Path.Combine(
                Path.GetDirectoryName(queueDirectory) ?? AppContext.BaseDirectory,
                "archive");

        Directory.CreateDirectory(queueDirectory);
        Directory.CreateDirectory(archiveDirectory);

        _logger.LogInformation(
            "Watching print queue: {path}",
            queueDirectory);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var jsonFiles = Directory.GetFiles(queueDirectory, "*.json");

                foreach (var jsonFile in jsonFiles)
                {
                    if (_processingFiles.Contains(jsonFile))
                    {
                        continue;
                    }

                    _processingFiles.Add(jsonFile);

                    try
                    {
                        await Task.Delay(1000, stoppingToken);

                        var pdfFile = Path.ChangeExtension(jsonFile, ".pdf");
                        if (!File.Exists(pdfFile))
                        {
                            _logger.LogWarning("Found JSON sidecar {jsonFile} but missing PDF file. Archiving orphan.", jsonFile);
                            var archiveOrphanPath = Path.Combine(archiveDirectory, Path.GetFileName(jsonFile));
                            File.Move(jsonFile, archiveOrphanPath, true);
                            continue;
                        }

                        _logger.LogInformation("Detected print job: {pdfFile} with settings {jsonFile}", pdfFile, jsonFile);

                        var jsonContent = await File.ReadAllTextAsync(jsonFile, stoppingToken);
                        var settings = JsonSerializer.Deserialize<PrintJobSettings>(jsonContent, JsonOptions) ?? new PrintJobSettings();

                        var fileName = Path.GetFileName(pdfFile);
                        var correlation = TryParseCorrelation(fileName);

                        await _eventPipe.SendAsync(
                            new WorkerPrintEvent
                            {
                                Type = WorkerPrintEventType.PrintStarted,
                                TransactionId = correlation.TransactionId,
                                SpoolerCorrelationKey = correlation.SpoolerCorrelationKey,
                                FileName = fileName,
                                PrinterName = _settings.PrinterName
                            },
                            stoppingToken,
                            connectTimeoutMilliseconds: NodeConnectTimeoutMs);

                        var result = await _printService.PrintAsync(
                            new PrintJobRequest
                            {
                                FilePath = pdfFile,
                                PrinterName = _settings.PrinterName,
                                Settings = settings
                            },
                            stoppingToken);

                        if (!result.Success)
                        {
                            _logger.LogError(
                                "Queue print failed | Stage={stage} | Message={message} | File={file}",
                                result.FailureStage,
                                result.Message,
                                pdfFile);
                        }
                        else
                        {
                            _logger.LogInformation("Queue print succeeded: {file}", pdfFile);
                        }

                        await _eventPipe.SendAsync(
                            new WorkerPrintEvent
                            {
                                Type = result.Success
                                    ? WorkerPrintEventType.PrintSucceeded
                                    : WorkerPrintEventType.PrintFailed,
                                TransactionId = correlation.TransactionId,
                                SpoolerCorrelationKey = correlation.SpoolerCorrelationKey,
                                FileName = fileName,
                                PrinterName = _settings.PrinterName,
                                FailureStage = result.Success ? null : result.FailureStage.ToString(),
                                Message = result.Success ? "Print completed" : result.Message
                            },
                            stoppingToken,
                            connectTimeoutMilliseconds: NodeConnectTimeoutMs);

                        var archivePdfPath = Path.Combine(archiveDirectory, Path.GetFileName(pdfFile));
                        var archiveJsonPath = Path.Combine(archiveDirectory, Path.GetFileName(jsonFile));

                        File.Move(pdfFile, archivePdfPath, true);
                        File.Move(jsonFile, archiveJsonPath, true);

                        _logger.LogInformation("Files archived successfully");
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Queue watcher failed while processing file: {file}", jsonFile);
                    }
                    finally
                    {
                        _processingFiles.Remove(jsonFile);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Queue watcher main loop failed");
            }

            await Task.Delay(2000, stoppingToken);
        }
    }

    public static (string? TransactionId, string? SpoolerCorrelationKey) TryParseCorrelation(
        string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var parts = baseName.Split('_', 3, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
        {
            return (null, null);
        }

        return (parts[0], parts[1]);
    }
}
