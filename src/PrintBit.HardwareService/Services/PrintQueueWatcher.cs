using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;
using PrintBit.Shared.Printing;

namespace PrintBit.HardwareService.Services;

public class PrintQueueWatcher : BackgroundService
{
    private readonly System.Collections.Generic.HashSet<string> _processingFiles = [];
    private readonly System.Collections.Generic.HashSet<string> _quarantinedFiles = [];
    private readonly ILogger<PrintQueueWatcher> _logger;
    private readonly IJobOrchestrator _orchestrator;
    private readonly HardwareSettings _settings;

    public PrintQueueWatcher(
        ILogger<PrintQueueWatcher> logger,
        IJobOrchestrator orchestrator,
        IOptions<HardwareSettings> options)
    {
        _logger = logger;
        _orchestrator = orchestrator;
        _settings = options.Value;
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
                    if (_processingFiles.Contains(jsonFile) || _quarantinedFiles.Contains(jsonFile) || !IsPrintJobSidecar(jsonFile)) continue;

                    _processingFiles.Add(jsonFile);
                    try
                    {
                        await Task.Delay(1000, stoppingToken);

                        var pdfFile = Path.ChangeExtension(jsonFile, ".pdf");
                        if (!File.Exists(pdfFile))
                        {
                            _logger.LogWarning("Found JSON sidecar {jsonFile} but missing PDF file. Moving to failed.", jsonFile);
                            SafelyMoveToFailed(jsonFile, pdfFile, failedDirectory);
                            continue;
                        }

                        _logger.LogInformation("Detected print job: {pdfFile}", pdfFile);
                        var jsonContent = await File.ReadAllTextAsync(jsonFile, stoppingToken);
                        if (!PrintJobSidecarValidator.TryParse(jsonContent, jsonFile, out var printSettings, out var validationError))
                        {
                            _logger.LogWarning(
                                "Rejecting invalid print sidecar {jsonFile}: {validationError}. Moving to failed.",
                                jsonFile,
                                validationError);
                            SafelyMoveToFailed(jsonFile, pdfFile, failedDirectory);
                            continue;
                        }

                        var request = new PrintJobRequest
                        {
                            FilePath = pdfFile,
                            PrinterName = _settings.PrinterName,
                            Settings = printSettings!
                        };

                        var result = await _orchestrator.ProcessJobAsync(request, jsonFile, stoppingToken);
                        if (result.Success)
                        {
                            SafelyDeleteFiles(jsonFile, pdfFile);
                            _logger.LogInformation("Print job processed successfully. Cleared sidecars.");
                        }
                        else
                        {
                            SafelyMoveToFailed(jsonFile, pdfFile, failedDirectory);
                            _logger.LogWarning("Print job failed. Files moved to failed directory.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process queue file {file}", jsonFile);
                        var pdfFile = Path.ChangeExtension(jsonFile, ".pdf");
                        SafelyMoveToFailed(jsonFile, pdfFile, failedDirectory);
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

    private void SafelyMoveToFailed(string jsonFile, string pdfFile, string failedDirectory)
    {
        bool movedJson = MoveOrCopyDelete(jsonFile, Path.Combine(failedDirectory, Path.GetFileName(jsonFile)));
        bool movedPdf = !File.Exists(pdfFile) || MoveOrCopyDelete(pdfFile, Path.Combine(failedDirectory, Path.GetFileName(pdfFile)));

        if (!movedJson || !movedPdf)
        {
            _logger.LogWarning("Could not move all failed sidecar files ({jsonFile}). Quarantining from watcher loop.", jsonFile);
            _quarantinedFiles.Add(jsonFile);
        }
    }

    private bool MoveOrCopyDelete(string source, string destination)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (!File.Exists(source)) return true;
                File.Move(source, destination, overwrite: true);
                return true;
            }
            catch
            {
                try
                {
                    File.Copy(source, destination, overwrite: true);
                    File.Delete(source);
                    return true;
                }
                catch
                {
                    Thread.Sleep(200);
                }
            }
        }
        return false;
    }

    private void SafelyDeleteFiles(string jsonFile, string pdfFile)
    {
        for (int i = 0; i < 3; i++)
        {
            try { if (File.Exists(pdfFile)) File.Delete(pdfFile); } catch { }
            try { if (File.Exists(jsonFile)) File.Delete(jsonFile); } catch { }
            if (!File.Exists(pdfFile) && !File.Exists(jsonFile)) return;
            Thread.Sleep(200);
        }
        if (File.Exists(jsonFile))
        {
            _logger.LogWarning("Could not delete sidecar {jsonFile} after success/cancel. Quarantining.", jsonFile);
            _quarantinedFiles.Add(jsonFile);
        }
    }

    private static bool IsPrintJobSidecar(string jsonFile)
    {
        var fileName = Path.GetFileName(jsonFile);
        var (tx, spool) = PrintJobFileName.TryParseCorrelation(fileName);
        return tx is not null && spool is not null;
    }
}
