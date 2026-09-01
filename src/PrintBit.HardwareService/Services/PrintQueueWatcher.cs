using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Infrastructure.Windows.PowerMonitoring;
using PrintBit.Shared.Configurations;
using PrintBit.Shared.Printing;

namespace PrintBit.HardwareService.Services;

public class PrintQueueWatcher : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly System.Collections.Generic.HashSet<string> _processingFiles = [];
    private readonly ILogger<PrintQueueWatcher> _logger;
    private readonly IJobOrchestrator _orchestrator;
    private readonly HardwareSettings _settings;
    private readonly IPowerSafetyGate _powerSafetyGate;

    public PrintQueueWatcher(
        ILogger<PrintQueueWatcher> logger,
        IJobOrchestrator orchestrator,
        IOptions<HardwareSettings> options,
        IPowerSafetyGate powerSafetyGate)
    {
        _logger = logger;
        _orchestrator = orchestrator;
        _settings = options.Value;
        _powerSafetyGate = powerSafetyGate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queueDirectory = Path.GetFullPath(_settings.PrintQueueDirectory);
        var failedDirectory = !string.IsNullOrWhiteSpace(_settings.FailedDirectory)
            ? Path.GetFullPath(_settings.FailedDirectory)
            : Path.Combine(Path.GetDirectoryName(queueDirectory) ?? AppContext.BaseDirectory, "failed");

        Directory.CreateDirectory(queueDirectory);
        Directory.CreateDirectory(failedDirectory);

        _logger.LogInformation("Watching print queue: {path}", queueDirectory);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var jsonFiles = Directory.GetFiles(queueDirectory, "*.json");
                foreach (var jsonFile in jsonFiles)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    if (_processingFiles.Contains(jsonFile) || !IsPrintJobSidecar(jsonFile)) continue;

                    _processingFiles.Add(jsonFile);
                    try
                    {
                        await Task.Delay(1000, stoppingToken);

                        var pdfFile = Path.ChangeExtension(jsonFile, ".pdf");
                        if (!File.Exists(pdfFile))
                        {
                            _logger.LogWarning("Found JSON sidecar {jsonFile} but missing PDF file. Moving to failed.", jsonFile);
                            File.Move(jsonFile, Path.Combine(failedDirectory, Path.GetFileName(jsonFile)), true);
                            continue;
                        }

                        _logger.LogInformation("Detected print job: {pdfFile}", pdfFile);
                        var jsonContent = await File.ReadAllTextAsync(jsonFile, stoppingToken);
                        var printSettings = JsonSerializer.Deserialize<PrintJobSettings>(jsonContent, JsonOptions) ?? new PrintJobSettings();

                        var request = new PrintJobRequest
                        {
                            FilePath = pdfFile,
                            PrinterName = _settings.PrinterName,
                            Settings = printSettings
                        };

                        var lease = _powerSafetyGate.TryAcquirePrintLease();
                        if (lease is null)
                        {
                            _logger.LogWarning("Power safety gate closed; postponing dispatch for {pdfFile}", pdfFile);
                            continue;
                        }

                        PrintBit.Infrastructure.Services.PrintService.PrintJobResult result;
                        using (lease)
                        {
                            result = await _orchestrator.ProcessJobAsync(request, jsonFile, stoppingToken);
                        }
                        if (result.Success)
                        {
                            try { File.Delete(pdfFile); } catch { }
                            try { File.Delete(jsonFile); } catch { }
                            _logger.LogInformation("Print job processed successfully. Cleared sidecars.");
                        }
                        else
                        {
                            File.Move(pdfFile, Path.Combine(failedDirectory, Path.GetFileName(pdfFile)), true);
                            File.Move(jsonFile, Path.Combine(failedDirectory, Path.GetFileName(jsonFile)), true);
                            _logger.LogWarning("Print job failed. Files moved to failed directory.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process queue file {file}", jsonFile);
                    }
                    finally
                    {
                        _processingFiles.Remove(jsonFile);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Print queue watcher loop hit error");
            }

            await Task.Delay(2000, stoppingToken);
        }
    }

    private static bool IsPrintJobSidecar(string jsonFile)
    {
        var fileName = Path.GetFileName(jsonFile);
        var (tx, spool) = PrintJobFileName.TryParseCorrelation(fileName);
        return tx is not null && spool is not null;
    }
}
