using System;
using System.Collections.Concurrent;
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
    // Signals a user-initiated pause. The dispatch loop awaits Gate.Task between
    // pages; PauseJobAsync sets _userPaused and ResumeJobAsync clears it and
    // completes the TCS. A fresh gate is swapped in on each resume so a second
    // pause on the same job gets its own awaitable signal.
    private sealed class PauseGate
    {
        private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool UserPaused;
        public Task Task => _tcs.Task;

        public void Signal()
        {
            UserPaused = false;
            _tcs.TrySetResult();
        }

        public PauseGate Reset()
        {
            _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            UserPaused = false;
            return this;
        }
    }

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeJobTokens = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _resumeSignals = new();
    private readonly ConcurrentDictionary<string, PauseGate> _pauseGates = new();
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

        try
        {
            // Split pages via qpdf
            try
            {
                await SplitPdfPagesAsync(request.FilePath, workDir, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "qpdf split failed");
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

        using var jobCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, jobCts.Token);
        _activeJobTokens[spoolerCorrelationKey] = jobCts;

        // Resume signal: set by ResumeJobAsync when the operator loads paper.
        // A fresh TCS is allocated per pause so that multiple pauses within
        // the same job each get their own awaitable signal.
        var resumeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _resumeSignals[spoolerCorrelationKey] = resumeTcs;

        // User-initiated pause gate (distinct from the hardware-fault patience
        // loop above). The dispatch loop checks this between pages.
        var pauseGate = _pauseGates.GetOrAdd(spoolerCorrelationKey, _ => new PauseGate()).Reset();

            try
            {
                for (int i = 0; i < manifest.Count; i++)
                {
                    linkedCts.Token.ThrowIfCancellationRequested();
                    var entry = manifest[i];

                    // 0. User-initiated pause hold. If the operator clicked Pause,
                    //    hold here between pages until Resume (gate signalled) or
                    //    Cancel (linked token trips ThrowIfCancellationRequested via
                    //    WaitAsync). The live spooler job, if any, was already paused
                    //    best-effort by PauseJobAsync.
                    if (pauseGate.UserPaused)
                    {
                        await EmitJobPausedAsync(transactionId, spoolerCorrelationKey, entry, completedCount, manifest.Count, "User paused print job", "user", linkedCts.Token);
                        try
                        {
                            await pauseGate.Task.WaitAsync(linkedCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        await EmitJobResumedAsync(transactionId, spoolerCorrelationKey, entry, completedCount, manifest.Count, linkedCts.Token);
                    }

                    // 1. Pre-flight health check
                    if (!_healthMonitor.IsHealthy(request.PrinterName, out _, out _))
                    {
                        // Auto-attempt to clear stale EPSON error state (popup, WinSpool, WMI cache)
                        // before deciding to enter patience mode. This handles leftover errors from
                        // a previous print session without requiring operator action.
                        _logger.LogWarning("Pre-flight: printer unhealthy; attempting auto-dismiss/reset before pause.");
                        await _healthMonitor.DismissAndResetAsync(request.PrinterName, linkedCts.Token);

                        // Re-check after recovery. Only enter patience if still unhealthy.
                        if (!_healthMonitor.IsHealthy(request.PrinterName, out _, out _))
                        {
                            await EnterPreFlightPauseAsync(request, entry, resumeTcs.Task, linkedCts.Token);
                        }
                        else
                        {
                            _logger.LogInformation("Pre-flight: printer recovered after auto-dismiss/reset. Proceeding without pause.");
                        }
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
                        onPaused: async (errMsg) => await EmitJobPausedAsync(transactionId, spoolerCorrelationKey, entry, completedCount, manifest.Count, errMsg, "hardware", linkedCts.Token),
                        onResumed: async () =>
                        {
                            // Replace the TCS so any subsequent pause on this job
                            // gets a fresh, unset signal.
                            var freshTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            _resumeSignals[spoolerCorrelationKey] = freshTcs;
                            resumeTcs = freshTcs;
                            await EmitJobResumedAsync(transactionId, spoolerCorrelationKey, entry, completedCount, manifest.Count, linkedCts.Token);
                        },
                        resumeSignalProvider: () => resumeTcs.Task,
                        linkedCts.Token);

                    entry.CompletedAt = DateTime.UtcNow;
                    if (printResult.State == PagePrintState.Completed)
                    {
                        entry.State = PagePrintState.Completed;
                        completedCount++;
                        await EmitPrintProgressAsync(transactionId, spoolerCorrelationKey, entry, completedCount, manifest.Count, linkedCts.Token);
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
            }
            catch (OperationCanceledException)
            {
                var nextIndex = manifest.FindIndex(m => m.State == PagePrintState.Pending || m.State == PagePrintState.Printing);
                if (nextIndex >= 0)
                {
                    CancelRemaining(manifest, nextIndex);
                    cancelledCount += manifest.Count - nextIndex;
                }
                failureMessage = "Job processing cancelled";
                finalFailureStage = PrintFailureStage.UserCancelled;
            }
            finally
            {
                _activeJobTokens.TryRemove(spoolerCorrelationKey, out _);
                _resumeSignals.TryRemove(spoolerCorrelationKey, out _);
                _pauseGates.TryRemove(spoolerCorrelationKey, out _);
            }

        var completedAt = DateTime.UtcNow;
        var outcome = "completed";
        if (failedCount > 0) outcome = "failed";
        else if (cancelledCount > 0 || manifest.Any(m => m.State == PagePrintState.Cancelled)) 
        {
            outcome = completedCount > 0 ? "partially_completed" : "cancelled";
        }

        if (outcome == "completed" && _healthMonitor.HasFatalHardwareError(request.PrinterName, out var fatalCode, out var fatalMsg))
        {
            _logger.LogWarning("Job pages finished but printer has fatal hardware error code {code}: {msg}", fatalCode, fatalMsg);
            outcome = "failed";
            failedCount = Math.Max(1, failedCount);
            failureMessage = $"Printer hardware error after print: {fatalMsg}";
            finalFailureStage = PrintFailureStage.HardwareError;
        }

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
            Message = outcome == "completed" ? "Print job completed successfully" : $"Print job finished with state: {outcome}. {failureMessage}"
        };

            if (_eventPipe is not null)
            {
                await _eventPipe.SendAsync(finalEvent, cancellationToken);
            }

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
        finally
        {
            // Clean work directory
            CleanWorkDirectory(workDir);
        }
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
            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();
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
        Task resumeSignal,
        CancellationToken cancellationToken)
    {
        var (tx, sck) = PrintJobFileName.TryParseCorrelation(Path.GetFileName(request.FilePath));
        await EmitJobPausedAsync(tx, sck, entry, entry.SequenceIndex, entry.SequenceIndex + 1, "Printer unhealthy before page dispatch", "hardware", cancellationToken);

        var deadline = DateTime.UtcNow.AddMinutes(_settings.PauseTimeoutMinutes);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Wake immediately if the operator clicks Resume, instead of waiting
            // the full 2-second polling interval.
            await Task.WhenAny(Task.Delay(2000, cancellationToken), resumeSignal);
            cancellationToken.ThrowIfCancellationRequested();

            if (_healthMonitor.IsHealthy(request.PrinterName, out _, out _))
            {
                // Replace TCS before emitting JobResumed so a subsequent
                // pause within the same job gets a fresh signal.
                if (_resumeSignals.TryGetValue(sck ?? string.Empty, out var oldTcs) && oldTcs.Task.IsCompleted)
                {
                    var freshTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _resumeSignals[sck ?? string.Empty] = freshTcs;
                }
                await EmitJobResumedAsync(tx, sck, entry, entry.SequenceIndex, entry.SequenceIndex + 1, cancellationToken);
                return;
            }
        }
        entry.State = PagePrintState.Cancelled;
        entry.ErrorMessage = "Pause timeout exceeded during pre-flight health wait";
    }

    private async Task EmitJobPausedAsync(string? tx, string? sck, PagePrintEntry entry, int completed, int total, string reason, string pauseReason, CancellationToken ct)
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
            FailureStage = pauseReason == "user" ? null : "HardwareError",
            Message = reason,
            ErrorMessage = pauseReason == "user" ? null : reason,
            PauseReason = pauseReason,
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

    public Task PauseJobAsync(string spoolerCorrelationKey, string reason)
    {
        if (string.IsNullOrWhiteSpace(spoolerCorrelationKey))
            return Task.CompletedTask;

        if (!_pauseGates.TryGetValue(spoolerCorrelationKey, out var gate))
        {
            _logger.LogInformation("Pause requested for key {Key} but no active job found.", spoolerCorrelationKey);
            return Task.CompletedTask;
        }

        gate.UserPaused = true;
        _logger.LogInformation("User pause set for job key {Key}: {Reason}", spoolerCorrelationKey, reason);

        // Best-effort: pause the live spooler job so a page already handed to
        // the spooler halts mid-way. The between-pages dispatch hold (B3) is
        // the guaranteed mechanism regardless of whether this succeeds.
        if (!string.IsNullOrWhiteSpace(_settings.PrinterName))
        {
            _healthMonitor.PauseMatchingJobs(_settings.PrinterName, spoolerCorrelationKey, null);
        }

        return Task.CompletedTask;
    }

    public async Task ResumeJobAsync(string spoolerCorrelationKey)
    {
        _logger.LogInformation("Resume requested for job key {Key}", spoolerCorrelationKey);

        var isUserPaused = _pauseGates.TryGetValue(spoolerCorrelationKey, out var gate) && gate.UserPaused;

        if (isUserPaused)
        {
            // User-initiated pause: resume the live spooler job and signal the
            // dispatch-loop gate. Only dismiss/reset the printer if it is
            // actually unhealthy (don't touch a healthy printer).
            if (!string.IsNullOrWhiteSpace(_settings.PrinterName))
            {
                _healthMonitor.ResumeMatchingJobs(_settings.PrinterName, spoolerCorrelationKey, null);
                if (_healthMonitor.HasFatalHardwareError(_settings.PrinterName, out _, out _))
                {
                    await _healthMonitor.DismissAndResetAsync(_settings.PrinterName, CancellationToken.None);
                }
            }
            gate!.Signal();
        }
        else
        {
            // Hardware-fault pause: dismiss/reset the Epson error and signal
            // the patience-loop TCS so it wakes immediately.
            if (!string.IsNullOrWhiteSpace(_settings.PrinterName))
            {
                await _healthMonitor.DismissAndResetAsync(_settings.PrinterName, CancellationToken.None);
            }

            if (_resumeSignals.TryGetValue(spoolerCorrelationKey, out var tcs))
            {
                tcs.TrySetResult();
            }
            else
            {
                _logger.LogWarning("Resume requested for key {Key} but no resume signal found (job may have already finished)", spoolerCorrelationKey);
            }
        }
    }

    public Task CancelActiveJobAsync(string spoolerCorrelationKey, string reason)
    {
        if (!string.IsNullOrWhiteSpace(spoolerCorrelationKey) && _activeJobTokens.TryGetValue(spoolerCorrelationKey, out var cts))
        {
            _logger.LogInformation("Signalling cancellation token for job key {Key} due to: {Reason}", spoolerCorrelationKey, reason);
            try
            {
                cts.Cancel();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error signalling cancellation token for job key {Key}", spoolerCorrelationKey);
            }
        }
        else
        {
            _logger.LogInformation("Cancel requested for job key {Key}, but no active CTS found.", spoolerCorrelationKey);
        }
        return Task.CompletedTask;
    }
}
