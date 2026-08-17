using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Infrastructure.IPC;
using PrintBit.Shared.Configurations;
using PrintBit.Shared.Printing;

namespace PrintBit.Infrastructure.Services.PrintService;

public class JobOrchestrator : IJobOrchestrator
{
    private readonly ILogger<JobOrchestrator> _logger;
    private readonly HardwareSettings _settings;
    private readonly IPagePrinter _pagePrinter;
    private readonly IPrinterHealthMonitor _healthMonitor;
    private readonly WorkerEventPipeClient _eventPipe;

    public JobOrchestrator(
        ILogger<JobOrchestrator> logger,
        IOptions<HardwareSettings> options,
        IPagePrinter pagePrinter,
        IPrinterHealthMonitor healthMonitor,
        WorkerEventPipeClient eventPipe)
    {
        _logger = logger;
        _settings = options.Value;
        _pagePrinter = pagePrinter;
        _healthMonitor = healthMonitor;
        _eventPipe = eventPipe;
    }

    public async Task<PrintJobResult> ProcessJobAsync(
        PrintJobRequest request,
        string jsonFilePath,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(request.FilePath);
        var (transactionId, spoolerCorrelationKey) = PrintJobFileName.TryParseCorrelation(fileName);

        // Pre-validate
        if (transactionId is null || spoolerCorrelationKey is null)
        {
            return PrintJobResult.Failed(PrintFailureStage.Validation, "Filename does not match the tx_spool layout");
        }

        var pdfPageCount = PdfPageCounter.Count(request.FilePath);
        if (pdfPageCount is null || pdfPageCount.Value == 0)
        {
            return PrintJobResult.Failed(PrintFailureStage.Validation, "Could not determine PDF page count or PDF is corrupt");
        }

        var workDir = Path.Combine(Path.GetDirectoryName(request.FilePath) ?? ".", ".work", $"{transactionId}_{spoolerCorrelationKey}");
        if (Directory.Exists(workDir))
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
        Directory.CreateDirectory(workDir);

        // Split pages via qpdf
        try
        {
            await SplitPdfPagesAsync(request.FilePath, workDir, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "qpdf split failed");
            CleanWorkDirectory(workDir);
            return PrintJobResult.Failed(PrintFailureStage.ProcessExit, $"qpdf split failed: {ex.Message}");
        }

        var pagesToPrint = GetPagesInRange(pdfPageCount.Value, request.Settings.PageRange);
        var totalCopies = Math.Max(1, request.Settings.Copies);
        
        // Build Manifest (Collated sequence)
        var manifest = new List<PagePrintEntry>();
        int sequenceIndex = 0;
        for (int c = 1; c <= totalCopies; c++)
        {
            foreach (var pageNum in pagesToPrint)
            {
                manifest.Add(new PagePrintEntry
                {
                    PageNumber = pageNum,
                    CopyNumber = c,
                    SequenceIndex = sequenceIndex++,
                    State = PagePrintState.Pending
                });
            }
        }

        var startedAt = DateTime.UtcNow;
        int completedCount = 0;
        int cancelledCount = 0;
        int failedCount = 0;
        string? failureMessage = null;
        PrintFailureStage finalFailureStage = PrintFailureStage.None;

        // Emit Print Started Event
        if (_eventPipe is not null)
        {
            await _eventPipe.SendAsync(new WorkerPrintEvent
            {
                Type = WorkerPrintEventType.PrintStarted,
                TransactionId = transactionId,
                SpoolerCorrelationKey = spoolerCorrelationKey,
                PrinterName = request.PrinterName,
                FileName = fileName,
                TotalExpected = manifest.Count,
                TotalCopies = totalCopies,
                TimestampUtc = DateTime.UtcNow
            }, cancellationToken);
        }

        for (int i = 0; i < manifest.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = manifest[i];

            // 1. Pre-flight health check
            if (!_healthMonitor.IsHealthy(request.PrinterName, out _, out _))
            {
                await EnterPreFlightPauseAsync(request, entry, cancellationToken);
            }

            if (entry.State == PagePrintState.Cancelled)
            {
                CancelRemaining(manifest, i);
                cancelledCount += manifest.Count - i;
                failureMessage = "Cancelled during pre-flight pause wait timeout";
                finalFailureStage = PrintFailureStage.HardwareError;
                break;
            }

            // Find split page file
            var pageFilePath = FindSplitPageFile(workDir, entry.PageNumber);
            if (pageFilePath is null || !File.Exists(pageFilePath))
            {
                entry.State = PagePrintState.Failed;
                entry.ErrorMessage = $"Split page file for page {entry.PageNumber} not found";
                failedCount++;
                CancelRemaining(manifest, i + 1);
                failureMessage = entry.ErrorMessage;
                finalFailureStage = PrintFailureStage.Validation;
                break;
            }

            entry.State = PagePrintState.Printing;
            entry.StartedAt = DateTime.UtcNow;

            var printResult = await _pagePrinter.PrintPageAsync(
                pageFilePath,
                request.PrinterName,
                entry.SequenceIndex,
                onPaused: async (errMsg) => await EmitJobPausedAsync(transactionId, spoolerCorrelationKey, entry, completedCount, manifest.Count, errMsg, cancellationToken),
                onResumed: async () => await EmitJobResumedAsync(transactionId, spoolerCorrelationKey, entry, completedCount, manifest.Count, cancellationToken),
                cancellationToken);

            entry.CompletedAt = DateTime.UtcNow;
            if (printResult.State == PagePrintState.Completed)
            {
                entry.State = PagePrintState.Completed;
                completedCount++;
                await EmitPrintProgressAsync(transactionId, spoolerCorrelationKey, entry, completedCount, manifest.Count, cancellationToken);
            }
            else if (printResult.State == PagePrintState.Cancelled)
            {
                entry.State = PagePrintState.Cancelled;
                entry.ErrorMessage = printResult.ErrorMessage;
                cancelledCount++;
                CancelRemaining(manifest, i + 1);
                failureMessage = printResult.ErrorMessage;
                finalFailureStage = printResult.FailureStage;
                break;
            }
            else
            {
                entry.State = PagePrintState.Failed;
                entry.ErrorMessage = printResult.ErrorMessage;
                failedCount++;
                CancelRemaining(manifest, i + 1);
                failureMessage = printResult.ErrorMessage;
                finalFailureStage = printResult.FailureStage;
                break;
            }
        }

        var completedAt = DateTime.UtcNow;
        var outcome = "completed";
        if (failedCount > 0) outcome = "failed";
        else if (cancelledCount > 0 || manifest.Any(m => m.State == PagePrintState.Cancelled)) outcome = "partially_completed";

        var pageResults = manifest.Select(m => new WorkerPrintPageResult
        {
            Page = m.PageNumber,
            Copy = m.CopyNumber,
            State = m.State.ToString().ToLowerInvariant()
        }).ToList();

        // Emit final JobCompleted event
        var finalEvent = new WorkerPrintEvent
        {
            Type = WorkerPrintEventType.JobCompleted,
            TransactionId = transactionId,
            SpoolerCorrelationKey = spoolerCorrelationKey,
            PrinterName = request.PrinterName,
            FileName = fileName,
            Outcome = outcome,
            TotalPages = pdfPageCount,
            TotalCopies = totalCopies,
            TotalExpected = manifest.Count,
            CompletedCount = completedCount,
            CancelledCount = manifest.Count(m => m.State == PagePrintState.Cancelled),
            FailedCount = failedCount,
            Pages = pageResults,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            FailureStage = finalFailureStage == PrintFailureStage.None ? null : finalFailureStage.ToString(),
            Message = outcome == "completed" ? "Print job completed successfully" : $"Print job finished with state: {outcome}. {failureMessage}"
        };

        if (_eventPipe is not null)
        {
            await _eventPipe.SendAsync(finalEvent, cancellationToken);
        }

        // Clean work directory
        CleanWorkDirectory(workDir);

        if (outcome == "failed")
        {
            return PrintJobResult.Failed(finalFailureStage, failureMessage ?? "Job execution failed");
        }

        return new PrintJobResult
        {
            Success = true,
            Message = $"Print job {outcome}",
            SumatraProcessSucceeded = true,
            VerificationSucceeded = true,
            PagesPrinted = completedCount,
            TotalPages = manifest.Count
        };
    }

    protected virtual async Task SplitPdfPagesAsync(string filePath, string workDir, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _settings.QpdfPath,
            Arguments = $"--split-pages=1 \"{filePath}\" \"{Path.Combine(workDir, "page.pdf")}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        using var splitTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.PdfSplitTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, splitTimeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw;
        }

        if (process.ExitCode != 0)
        {
            var err = await process.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException($"qpdf split failed with exit code {process.ExitCode}: {err}");
        }
    }

    private static List<int> GetPagesInRange(int pdfPageCount, string? pageRange)
    {
        var pages = new List<int>();
        if (string.IsNullOrWhiteSpace(pageRange))
        {
            for (int i = 1; i <= pdfPageCount; i++) pages.Add(i);
            return pages;
        }

        var parts = pageRange.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (part.Contains('-'))
            {
                var subParts = part.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (subParts.Length == 2 && int.TryParse(subParts[0], out var start) && int.TryParse(subParts[1], out var end))
                {
                    var step = start <= end ? 1 : -1;
                    for (int i = start; start <= end ? i <= end : i >= end; i += step)
                    {
                        if (i >= 1 && i <= pdfPageCount) pages.Add(i);
                    }
                }
            }
            else
            {
                if (int.TryParse(part, out var p) && p >= 1 && p <= pdfPageCount) pages.Add(p);
            }
        }
        return pages;
    }

    private static string? FindSplitPageFile(string workDir, int pageNumber)
    {
        for (int length = 1; length <= 5; length++)
        {
            var padded = pageNumber.ToString().PadLeft(length, '0');
            var path = Path.Combine(workDir, $"page-{padded}.pdf");
            if (File.Exists(path)) return path;
        }
        var files = Directory.GetFiles(workDir, $"page-*{pageNumber}.pdf");
        return files.Length > 0 ? files[0] : null;
    }

    private void CancelRemaining(List<PagePrintEntry> manifest, int startIndex)
    {
        for (int i = startIndex; i < manifest.Count; i++)
        {
            manifest[i].State = PagePrintState.Cancelled;
        }
    }

    private async Task EnterPreFlightPauseAsync(
        PrintJobRequest request,
        PagePrintEntry entry,
        CancellationToken cancellationToken)
    {
        var (tx, sck) = PrintJobFileName.TryParseCorrelation(Path.GetFileName(request.FilePath));
        await EmitJobPausedAsync(tx, sck, entry, entry.SequenceIndex, entry.SequenceIndex + 1, "Printer unhealthy before page dispatch", cancellationToken);

        var deadline = DateTime.UtcNow.AddMinutes(_settings.PauseTimeoutMinutes);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(2000, cancellationToken);

            if (_healthMonitor.IsHealthy(request.PrinterName, out _, out _))
            {
                await EmitJobResumedAsync(tx, sck, entry, entry.SequenceIndex, entry.SequenceIndex + 1, cancellationToken);
                return;
            }
        }
        entry.State = PagePrintState.Cancelled;
        entry.ErrorMessage = "Pause timeout exceeded during pre-flight health wait";
    }

    private async Task EmitJobPausedAsync(string? tx, string? sck, PagePrintEntry entry, int completed, int total, string reason, CancellationToken ct)
    {
        var evt = new WorkerPrintEvent
        {
            Type = WorkerPrintEventType.JobPaused,
            TransactionId = tx,
            SpoolerCorrelationKey = sck,
            FailedPageNumber = entry.PageNumber,
            FailedCopyNumber = entry.CopyNumber,
            CompletedCount = completed,
            TotalCount = total,
            ErrorMessage = reason,
            TimestampUtc = DateTime.UtcNow
        };
        if (_eventPipe is not null)
        {
            await _eventPipe.SendAsync(evt, ct);
        }
    }

    private async Task EmitJobResumedAsync(string? tx, string? sck, PagePrintEntry entry, int completed, int total, CancellationToken ct)
    {
        var evt = new WorkerPrintEvent
        {
            Type = WorkerPrintEventType.JobResumed,
            TransactionId = tx,
            SpoolerCorrelationKey = sck,
            ResumingPageNumber = entry.PageNumber,
            ResumingCopyNumber = entry.CopyNumber,
            CompletedCount = completed,
            TotalCount = total,
            TimestampUtc = DateTime.UtcNow
        };
        if (_eventPipe is not null)
        {
            await _eventPipe.SendAsync(evt, ct);
        }
    }

    private async Task EmitPrintProgressAsync(string? tx, string? sck, PagePrintEntry entry, int completed, int total, CancellationToken ct)
    {
        var evt = new WorkerPrintEvent
        {
            Type = WorkerPrintEventType.PrintProgress,
            TransactionId = tx,
            SpoolerCorrelationKey = sck,
            PageNumber = entry.PageNumber,
            CopyNumber = entry.CopyNumber,
            CompletedCount = completed,
            TotalCount = total,
            TimestampUtc = DateTime.UtcNow
        };
        if (_eventPipe is not null)
        {
            await _eventPipe.SendAsync(evt, ct);
        }
    }

    private void CleanWorkDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to clean working directory {dir}", dir); }
    }
}
