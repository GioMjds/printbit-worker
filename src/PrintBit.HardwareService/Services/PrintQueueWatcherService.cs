using System.Text.Json;
using Microsoft.Extensions.Options;
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;

namespace PrintBit.HardwareService.Services;

public class PrintQueueWatcherService : BackgroundService
{
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
        var queueDirectory = Path.GetFullPath(_settings.PrintQueueDirectory);
        var failedDirectory =
            Path.Combine(
                Path.GetDirectoryName(queueDirectory) ?? AppContext.BaseDirectory,
                "failed");

        Directory.CreateDirectory(queueDirectory);
        Directory.CreateDirectory(failedDirectory);

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

                    // Foreign JSON in the queue directory (e.g. appsettings.json, project.assets.json)
                    // must not be treated as a print job. Only files matching the
                    // tx-<id>_spool-<id>_<ts>.json shape are real print sidecars.
                    if (!IsPrintJobSidecar(jsonFile))
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
                            _logger.LogWarning("Found JSON sidecar {jsonFile} but missing PDF file. Moving to failed.", jsonFile);
                            var failedOrphanPath = Path.Combine(failedDirectory, Path.GetFileName(jsonFile));
                            File.Move(jsonFile, failedOrphanPath, true);
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
                            stoppingToken);

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
                                SpoolerJobId = result.SpoolerJobId,
                                FailureStage = result.Success ? null : result.FailureStage.ToString(),
                                Message = result.Success ? "Print completed" : result.Message
                            },
                            stoppingToken);

                        if (result.Success)
                        {
                            File.Delete(pdfFile);
                            File.Delete(jsonFile);

                            _logger.LogInformation("Print succeeded; files deleted: {file}", fileName);
                        }
                        else
                        {
                            var failedPdfPath = Path.Combine(failedDirectory, Path.GetFileName(pdfFile));
                            var failedJsonPath = Path.Combine(failedDirectory, Path.GetFileName(jsonFile));

                            File.Move(pdfFile, failedPdfPath, true);
                            File.Move(jsonFile, failedJsonPath, true);

                            _logger.LogWarning("Print failed; files moved to failed directory: {file}", fileName);
                        }
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

    private static bool IsPrintJobSidecar(string jsonFile)
    {
        var fileName = Path.GetFileName(jsonFile);

        // Sidecar file is "tx-..._spool-..._...json". TryParseCorrelation returns
        // a non-null pair only when the first two underscore-separated segments are
        // non-empty, which is the contract we depend on.
        var (transactionId, spoolKey) = TryParseCorrelation(fileName);

        return transactionId is not null && spoolKey is not null;
    }
}