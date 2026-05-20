using Microsoft.Extensions.Options;
using PrintBit.Application.Events;
using PrintBit.Application.Services;
using PrintBit.Shared.Configurations;

namespace PrintBit.HardwareService.Services;

public class PrintQueueWatcherService : BackgroundService
{
    private readonly HashSet<string> _processingFiles = [];

    private readonly ILogger<PrintQueueWatcherService> _logger;

    private readonly HardwareOrchestrator _orchestrator;

    private readonly HardwareSettings _settings;

    public PrintQueueWatcherService(
        ILogger<PrintQueueWatcherService> logger,
        HardwareOrchestrator orchestrator,
        IOptions<HardwareSettings> options)
    {
        _logger = logger;
        _orchestrator = orchestrator;
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

                        var accepted = await _orchestrator.HandlePrintRequestAsync(
                            new StartPrintEvent
                            {
                                FilePath = file
                            },
                            source: "queue",
                            cancellationToken: stoppingToken);

                        if (!accepted)
                        {
                            _logger.LogWarning(
                                "Queue print request rejected by transaction gate; file retained for retry: {file}",
                                file);

                            continue;
                        }

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
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Queue watcher main loop failed");
            }

            await Task.Delay(2000, stoppingToken);
        }
    }
}
