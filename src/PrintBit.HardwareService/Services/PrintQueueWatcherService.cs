using Microsoft.Extensions.Options;
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;

namespace PrintBit.HardwareService.Services;

public class PrintQueueWatcherService : BackgroundService
{
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
                var pdfFiles = Directory.GetFiles(queueDirectory, "*.pdf");

                foreach (var file in pdfFiles)
                {
                    if (_processingFiles.Contains(file))
                    {
                        continue;
                    }

                    _processingFiles.Add(file);

                    try
                    {
                        await Task.Delay(1000, stoppingToken);

                        _logger.LogInformation(
                            "Detected print file: {file}",
                            file);

                        var fileName = Path.GetFileName(file);
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
                                FilePath = file,
                                PrinterName = _settings.PrinterName
                            },
                            stoppingToken);

                        if (!result.Success)
                        {
                            _logger.LogError(
                                "Queue print failed | Stage={stage} | Message={message} | File={file}",
                                result.FailureStage,
                                result.Message,
                                file);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "Queue print succeeded: {file}",
                                file);
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
                                FailureStage = result.Success
                                    ? null
                                    : result.FailureStage.ToString(),
                                Message = result.Success
                                    ? "Print completed"
                                    : result.Message
                            },
                            stoppingToken);

                        var archivePath =
                            Path.Combine(
                                archiveDirectory,
                                Path.GetFileName(file));

                        _logger.LogInformation(
                            "Archiving file to: {path}",
                            archivePath);

                        File.Move(
                            file,
                            archivePath,
                            true);

                        _logger.LogInformation(
                            "File archived successfully");
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "Queue watcher failed while processing file: {file}",
                            file);
                    }
                    finally
                    {
                        _processingFiles.Remove(file);
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
