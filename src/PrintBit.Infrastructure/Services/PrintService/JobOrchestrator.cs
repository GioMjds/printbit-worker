using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    private readonly WorkerEventPipeClient _eventPipe;
    private readonly IPrinterHealthMonitor _healthMonitor;

    public JobOrchestrator(
        ILogger<JobOrchestrator> logger,
        IOptions<HardwareSettings> options,
        IPagePrinter pagePrinter,
        WorkerEventPipeClient eventPipe,
        IPrinterHealthMonitor healthMonitor)
    {
        _logger = logger;
        _settings = options.Value;
        _pagePrinter = pagePrinter;
        _eventPipe = eventPipe;
        _healthMonitor = healthMonitor;
    }

    public async Task ProcessJobAsync(
        string pdfPath,
        string transactionId,
        string spoolerCorrelationKey,
        int copies,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Orchestrating job '{pdfPath}' (TX: {tx}, Copies: {copies})", pdfPath, transactionId, copies);

        var tempDir = Path.Combine(Path.GetTempPath(), $"PrintBitSplit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Step 1: Pre-flight Health Check
            if (!_healthMonitor.IsHealthy(_settings.PrinterName, out var winSpoolStatus, out var winSpoolDesc))
            {
                var errorMsg = $"Pre-flight printer check failed: wsStatus=0x{winSpoolStatus:X} ({winSpoolDesc})";
                _logger.LogError(errorMsg);
                await EmitFailureEventAsync(transactionId, spoolerCorrelationKey, "hardware_error", "pre_flight", errorMsg);
                return;
            }

            // Step 2: Split PDF using qpdf
            int totalPages = await SplitPdfAsync(pdfPath, tempDir, cancellationToken);
            if (totalPages <= 0)
            {
                var errorMsg = "Failed to split PDF or document contains no pages";
                _logger.LogError(errorMsg);
                await EmitFailureEventAsync(transactionId, spoolerCorrelationKey, "validation", "pdf_split", errorMsg);
                return;
            }

            int totalExpected = totalPages * copies;
            _logger.LogInformation("PDF split successful. Total pages: {totalPages}. Total copies to print: {totalExpected}", totalPages, totalExpected);

            // Step 3: Initialize Manifest
            var manifest = new List<PagePrintEntry>();
            int sequenceIndex = 0;
            for (int copy = 1; copy <= copies; copy++)
            {
                for (int page = 1; page <= totalPages; page++)
                {
                    manifest.Add(new PagePrintEntry
                    {
                        PageNumber = page,
                        CopyNumber = copy,
                        SequenceIndex = sequenceIndex++,
                        State = PagePrintState.Pending
                    });
                }
            }

            // Step 4: Emit Print Started Event
            await _eventPipe.SendAsync(new WorkerPrintEvent
            {
                Type = WorkerPrintEventType.PrintStarted,
                TransactionId = transactionId,
                SpoolerCorrelationKey = spoolerCorrelationKey,
                PrinterName = _settings.PrinterName,
                FileName = Path.GetFileName(pdfPath),
                TotalExpected = totalExpected,
                TotalCopies = copies,
                TimestampUtc = DateTime.UtcNow
            }, cancellationToken);

            // Step 5: Page loop
            bool hasPaused = false;
            foreach (var entry in manifest)
            {
                cancellationToken.ThrowIfCancellationRequested();

                entry.State = PagePrintState.Printing;
                entry.StartedAt = DateTime.UtcNow;

                // Emit progress event
                await _eventPipe.SendAsync(new WorkerPrintEvent
                {
                    Type = WorkerPrintEventType.PrintProgress,
                    TransactionId = transactionId,
                    SpoolerCorrelationKey = spoolerCorrelationKey,
                    PrinterName = _settings.PrinterName,
                    PageNumber = entry.PageNumber,
                    CopyNumber = entry.CopyNumber,
                    TimestampUtc = DateTime.UtcNow
                }, cancellationToken);

                var pageFile = Path.Combine(tempDir, $"page-{entry.PageNumber:D4}.pdf");

                var pageResult = await _pagePrinter.PrintPageAsync(
                    pageFile,
                    _settings.PrinterName,
                    entry.SequenceIndex,
                    onPaused: async (pausedReason) =>
                    {
                        hasPaused = true;
                        _logger.LogWarning("Print job paused: {reason}", pausedReason);
                        await _eventPipe.SendAsync(new WorkerPrintEvent
                        {
                            Type = WorkerPrintEventType.JobPaused,
                            TransactionId = transactionId,
                            SpoolerCorrelationKey = spoolerCorrelationKey,
                            PrinterName = _settings.PrinterName,
                            FailedPageNumber = entry.PageNumber,
                            FailedCopyNumber = entry.CopyNumber,
                            Message = pausedReason,
                            TimestampUtc = DateTime.UtcNow
                        }, cancellationToken);
                    },
                    onResumed: async () =>
                    {
                        _logger.LogInformation("Print job resumed.");
                        await _eventPipe.SendAsync(new WorkerPrintEvent
                        {
                            Type = WorkerPrintEventType.JobResumed,
                            TransactionId = transactionId,
                            SpoolerCorrelationKey = spoolerCorrelationKey,
                            PrinterName = _settings.PrinterName,
                            ResumingPageNumber = entry.PageNumber,
                            ResumingCopyNumber = entry.CopyNumber,
                            TimestampUtc = DateTime.UtcNow
                        }, cancellationToken);
                    },
                    cancellationToken);

                entry.State = pageResult.State;
                entry.ErrorMessage = pageResult.ErrorMessage;
                entry.CompletedAt = DateTime.UtcNow;

                if (pageResult.State == PagePrintState.Failed)
                {
                    _logger.LogError("Page {page} Copy {copy} failed: {err}", entry.PageNumber, entry.CopyNumber, pageResult.ErrorMessage);
                    await EmitFailureEventAsync(transactionId, spoolerCorrelationKey, pageResult.FailureStage.ToString().ToLowerInvariant(), "page_print", pageResult.ErrorMessage, entry.PageNumber, entry.CopyNumber);
                    return;
                }

                if (pageResult.State == PagePrintState.Cancelled)
                {
                    // User cancellation (Stop)
                    _logger.LogWarning("Job cancelled by user at Page {page} Copy {copy}", entry.PageNumber, entry.CopyNumber);
                    
                    // Mark remaining pages as Cancelled
                    foreach (var remaining in manifest.Where(m => m.State == PagePrintState.Pending))
                    {
                        remaining.State = PagePrintState.Cancelled;
                    }

                    await EmitJobCompletedEventAsync(transactionId, spoolerCorrelationKey, "cancelled", manifest);
                    return;
                }
            }

            // Step 6: Success Completion
            var outcome = hasPaused ? "partially_completed" : "completed";
            await EmitJobCompletedEventAsync(transactionId, spoolerCorrelationKey, outcome, manifest);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Print orchestration cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in print job orchestration");
            await EmitFailureEventAsync(transactionId, spoolerCorrelationKey, "system_error", "orchestration", ex.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to clean up temp split folder: {tempDir}", tempDir); }
        }
    }

    private async Task<int> SplitPdfAsync(string sourcePdf, string outputDir, CancellationToken cancellationToken)
    {
        var qpdfPath = _settings.QpdfPath;
        if (!File.Exists(qpdfPath))
        {
            _logger.LogError("qpdf path not found: {qpdf}", qpdfPath);
            return 0;
        }

        // qpdf command: qpdf --split-pages=1 source.pdf page-%04d.pdf
        var destinationPattern = Path.Combine(outputDir, "page-%04d.pdf");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = qpdfPath,
                Arguments = $"--split-pages=1 \"{sourcePdf}\" \"{destinationPattern}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.PdfSplitTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            process.Start();
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
            _logger.LogError("qpdf split exited with code {code}: {err}", process.ExitCode, err);
            return 0;
        }

        // Return number of files generated matching page-*.pdf
        return Directory.GetFiles(outputDir, "page-*.pdf").Length;
    }

    private async Task EmitFailureEventAsync(
        string transactionId,
        string spoolerCorrelationKey,
        string failureStage,
        string source,
        string? errorMsg,
        int? pageNumber = null,
        int? copyNumber = null)
    {
        await _eventPipe.SendAsync(new WorkerPrintEvent
        {
            Type = WorkerPrintEventType.PrintFailed,
            TransactionId = transactionId,
            SpoolerCorrelationKey = spoolerCorrelationKey,
            PrinterName = _settings.PrinterName,
            FailureStage = failureStage,
            Message = errorMsg,
            FailedPageNumber = pageNumber,
            FailedCopyNumber = copyNumber,
            TimestampUtc = DateTime.UtcNow
        });
    }

    private async Task EmitJobCompletedEventAsync(
        string transactionId,
        string spoolerCorrelationKey,
        string outcome,
        List<PagePrintEntry> manifest)
    {
        var completedCount = manifest.Count(m => m.State == PagePrintState.Completed);
        var cancelledCount = manifest.Count(m => m.State == PagePrintState.Cancelled);
        var failedCount = manifest.Count(m => m.State == PagePrintState.Failed);

        var pageResults = manifest.Select(m => new WorkerPrintPageResult
        {
            Page = m.PageNumber,
            Copy = m.CopyNumber,
            State = m.State.ToString().ToLowerInvariant()
        }).ToList();

        await _eventPipe.SendAsync(new WorkerPrintEvent
        {
            Type = WorkerPrintEventType.JobCompleted,
            TransactionId = transactionId,
            SpoolerCorrelationKey = spoolerCorrelationKey,
            PrinterName = _settings.PrinterName,
            Outcome = outcome,
            CompletedCount = completedCount,
            CancelledCount = cancelledCount,
            FailedCount = failedCount,
            TotalExpected = manifest.Count,
            Pages = pageResults,
            TimestampUtc = DateTime.UtcNow
        });
    }
}
