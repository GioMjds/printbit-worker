using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Shared.Configurations;
using PrintBit.Shared.Printing;

namespace PrintBit.Infrastructure.Services.PrintService;

public class PagePrinter : IPagePrinter
{
    private static readonly SemaphoreSlim PrintLock = new(1, 1);
    private readonly ILogger<PagePrinter> _logger;
    private readonly HardwareSettings _settings;
    private readonly IPrinterHealthMonitor _healthMonitor;

    public PagePrinter(
        ILogger<PagePrinter> logger,
        IOptions<HardwareSettings> options,
        IPrinterHealthMonitor healthMonitor)
    {
        _logger = logger;
        _settings = options.Value;
        _healthMonitor = healthMonitor;
    }

    public async Task<PagePrintResult> PrintPageAsync(
        string filePath,
        string printerName,
        int sequenceIndex,
        Func<string, Task> onPaused,
        Func<Task> onResumed,
        Func<Task> resumeSignalProvider,
        CancellationToken cancellationToken)
    {
        await PrintLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(filePath))
            {
                return new PagePrintResult { State = PagePrintState.Failed, FailureStage = PrintFailureStage.Validation, ErrorMessage = "Page file not found" };
            }

            var sumatraPath = _settings.SumatraPath;
            if (!File.Exists(sumatraPath))
            {
                return new PagePrintResult { State = PagePrintState.Failed, FailureStage = PrintFailureStage.Validation, ErrorMessage = "SumatraPDF executable not found" };
            }

            _logger.LogInformation("Dispatching SumatraPDF for page {filePath} on printer {printerName}", filePath, printerName);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = sumatraPath,
                    Arguments = $"-print-to \"{printerName}\" -silent \"{filePath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            try { process.Start(); }
            catch (Exception ex)
            {
                return new PagePrintResult { State = PagePrintState.Failed, FailureStage = PrintFailureStage.ProcessStart, ErrorMessage = ex.Message };
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.PrintTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                var stdOutTask = process.StandardOutput.ReadToEndAsync();
                var stdErrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                if (cancellationToken.IsCancellationRequested)
                {
                    return new PagePrintResult
                    {
                        State = PagePrintState.Cancelled,
                        FailureStage = PrintFailureStage.UserCancelled,
                        ErrorMessage = "Print job cancelled by user."
                    };
                }
                await _healthMonitor.RecoverAsync(cancellationToken);
                return new PagePrintResult { State = PagePrintState.Failed, FailureStage = PrintFailureStage.Timeout, ErrorMessage = "Sumatra process timeout" };
            }

            if (process.ExitCode != 0)
            {
                var err = await process.StandardError.ReadToEndAsync(cancellationToken);
                return new PagePrintResult { State = PagePrintState.Failed, FailureStage = PrintFailureStage.ProcessExit, ErrorMessage = err };
            }

            // Verification Loop (Patience Mode)
            return await VerifySpoolerPageLifecycleAsync(printerName, Path.GetFileName(filePath), onPaused, onResumed, resumeSignalProvider, cancellationToken);
        }
        finally
        {
            PrintLock.Release();
        }
    }

    private async Task<PagePrintResult> VerifySpoolerPageLifecycleAsync(
        string printerName,
        string documentName,
        Func<string, Task> onPaused,
        Func<Task> onResumed,
        Func<Task> resumeSignalProvider,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        var patienceDeadline = DateTime.UtcNow.AddMinutes(_settings.PauseTimeoutMinutes);

        bool inPatienceMode = false;
        bool observedActive = false;
        string? lastSpoolerJobId = null;

        try
        {
            while (DateTime.UtcNow < deadline || (inPatienceMode && DateTime.UtcNow < patienceDeadline))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (exists, statusMask, jobStatus, printed, total, jobId) = _healthMonitor.QueryJobStatus(printerName, documentName);
                if (exists)
                {
                    lastSpoolerJobId = jobId;

                    bool jobHasError = (statusMask & (0x2 | 0x40)) != 0;
                    bool fatalMonitorError = _healthMonitor.HasFatalHardwareError(printerName, out _, out var fatalMsg);
                    bool isDeleting = (statusMask & (0x4 | 0x100)) != 0 || jobStatus.Contains("Deleting", StringComparison.OrdinalIgnoreCase);
                    bool isNormalProgress = !jobHasError && !isDeleting;

                    if (isNormalProgress)
                    {
                        observedActive = true;
                        deadline = DateTime.UtcNow.AddSeconds(45);
                    }

                    if (jobHasError || fatalMonitorError)
                    {
                        if (!inPatienceMode)
                        {
                            inPatienceMode = true;
                            var errorMsg = fatalMonitorError ? fatalMsg : $"Spooler error status: {jobStatus} (0x{statusMask:X})";
                            await onPaused(errorMsg);
                        }

                        // Wake immediately on Resume click instead of waiting the full 2s poll.
                        // Re-fetch the live signal each iteration so a SECOND fault on the same
                        // page waits for a fresh Resume rather than seeing the previous (already
                        // completed) signal and auto-resuming. This is the stale-signal fix.
                        if (resumeSignalProvider().IsCompleted)
                        {
                            inPatienceMode = false;
                            await onResumed();
                            deadline = DateTime.UtcNow.AddSeconds(45);
                        }
                    }
                    else
                    {
                        if (inPatienceMode)
                        {
                            inPatienceMode = false;
                            await onResumed();
                            deadline = DateTime.UtcNow.AddSeconds(45);
                        }
                    }
                }
                else
                {
                    // Job cleared from spooler (printed normally, or EPSON driver fast-cleared it on paper-out).
                    if (observedActive || lastSpoolerJobId == null)
                    {
                        // High-risk case: job was never observed as active in the spooler before clearing.
                        // This is the EPSON fast-clear race — the driver removes the job from the spooler
                        // immediately on paper-out, before the page physically exits the printer. We
                        // cannot tell if the page printed successfully or paper ran out mid-feed.
                        // Use a longer guard and require multiple consecutive healthy polls before
                        // calling the page completed.
                        bool neverObserved = !observedActive && lastSpoolerJobId == null;
                        var guardDelaySeconds = neverObserved
                            ? Math.Max(8, _settings.PostClearGuardDelaySeconds)  // longer: was never in spooler
                            : Math.Max(6, _settings.PostClearGuardDelaySeconds); // 6s minimum: EPSON W-01 WMI propagation can take up to 4s after fast-clear

                        _logger.LogInformation(
                            "Job cleared from spooler; running post-clear hardware guard window ({delay}s, neverObserved={neverObserved})",
                            guardDelaySeconds, neverObserved);

                        // Initial settle delay: the EPSON W-01 (paper-out) state propagates to WMI
                        // DetectedErrorState ~1-4 seconds after the driver fast-clears the spooler job.
                        // Wait at least 3 seconds before the first health check so we don't poll
                        // during the window where the error hasn't surfaced yet.
                        const int InitialSettleMs = 3000;
                        await Task.WhenAny(Task.Delay(InitialSettleMs, cancellationToken), resumeSignalProvider());
                        cancellationToken.ThrowIfCancellationRequested();

                        // For a never-observed job (pure fast-clear), require two consecutive healthy
                        // snapshots separated by 1s to reduce single-sample false-positives.
                        int consecutiveHealthy = 0;
                        // Always require 2 consecutive healthy checks — the EPSON W-01 (paper-out)
                        // WMI state can arrive 1-4s after the driver fast-clears the spooler job,
                        // meaning a single healthy poll on page 2+ (observedActive=true) races past
                        // the error before it surfaces. Two checks separated by ~1s close that window.
                        int requiredConsecutiveHealthy = 2;

                        var guardEndTime = DateTime.UtcNow.AddSeconds(guardDelaySeconds - InitialSettleMs / 1000.0);

                        // Poll with fresh IsHealthy (not only cached _fatalErrorCode) to catch the EPSON
                        // paper-out fast-clear race: driver removes the job before the background monitor
                        // loop has had a chance to update its cached error state.
                        while (DateTime.UtcNow < guardEndTime || consecutiveHealthy < requiredConsecutiveHealthy)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            bool healthyNow = _healthMonitor.IsHealthy(printerName, out _, out _);
                            bool fatalNow = _healthMonitor.HasFatalHardwareError(printerName, out var errorCode, out var errorMsg);

                            if (!healthyNow || fatalNow)
                            {
                                consecutiveHealthy = 0;
                                _logger.LogWarning("Post-clear guard detected hardware error (code {code}): {msg}", errorCode, errorMsg);
                                var pauseMsg = errorMsg.Length > 0 ? errorMsg : "Paper out or hardware error after job cleared";
                                await onPaused(pauseMsg);

                                // Patience: wait for operator to load paper and click Resume
                                var paperPatienceEnd = DateTime.UtcNow.AddMinutes(_settings.PauseTimeoutMinutes);
                                while (DateTime.UtcNow < paperPatienceEnd)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    await Task.WhenAny(Task.Delay(2000, cancellationToken), resumeSignalProvider());
                                    cancellationToken.ThrowIfCancellationRequested();

                                    if (_healthMonitor.IsHealthy(printerName, out _, out _))
                                    {
                                        _logger.LogInformation("Printer recovered after post-clear paper-out. Signalling JobResumed.");
                                        await onResumed();
                                        // Return Completed — orchestrator will re-print this page on the next loop iteration
                                        return new PagePrintResult { State = PagePrintState.Completed, SpoolerJobId = lastSpoolerJobId };
                                    }
                                }

                                _logger.LogWarning("Post-clear paper-out patience timeout exceeded.");
                                _healthMonitor.CancelMatchingJobs(printerName, documentName, lastSpoolerJobId);
                                return new PagePrintResult
                                {
                                    State = PagePrintState.Failed,
                                    FailureStage = PrintFailureStage.HardwareError,
                                    ErrorMessage = $"Paper out patience timeout: {errorMsg}"
                                };
                            }

                            consecutiveHealthy++;
                            if (consecutiveHealthy >= requiredConsecutiveHealthy && DateTime.UtcNow >= guardEndTime)
                            {
                                break;
                            }

                            var delay = (int)Math.Min(1000, Math.Max(0, (guardEndTime - DateTime.UtcNow).TotalMilliseconds));
                            if (delay > 0 || consecutiveHealthy < requiredConsecutiveHealthy)
                            {
                                await Task.WhenAny(Task.Delay(Math.Max(delay, 1000), cancellationToken), resumeSignalProvider());
                                cancellationToken.ThrowIfCancellationRequested();
                            }
                        }

                        // Guard elapsed with sufficient healthy snapshots — page printed successfully
                        _logger.LogInformation("Post-clear guard passed ({healthy} healthy checks). Page confirmed printed.", consecutiveHealthy);
                        return new PagePrintResult { State = PagePrintState.Completed };
                    }

                    if (lastSpoolerJobId != null)
                    {
                        _logger.LogWarning("Spooler job {jobId} vanished without being observed active; treating as cancelled", lastSpoolerJobId);
                        return new PagePrintResult { State = PagePrintState.Cancelled, ErrorMessage = "Spooler job vanished without printing; likely cancelled by user" };
                    }
                }

                // Use WhenAny so the resume signal can wake us ahead of the 2s poll interval.
                await Task.WhenAny(Task.Delay(2000, cancellationToken), resumeSignalProvider());
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException)
        {
            if (lastSpoolerJobId != null || observedActive)
            {
                _logger.LogWarning("Spooler verification cancelled. Cancelling matching print jobs.");
                _healthMonitor.CancelMatchingJobs(printerName, documentName, lastSpoolerJobId);
            }
            throw;
        }

        if (inPatienceMode)
        {
            _logger.LogWarning("Patience timeout exceeded. Cancelling spooler job.");
            _healthMonitor.CancelMatchingJobs(printerName, documentName, lastSpoolerJobId);
            return new PagePrintResult { State = PagePrintState.Cancelled, FailureStage = PrintFailureStage.Timeout, ErrorMessage = "Patience timeout exceeded" };
        }

        return new PagePrintResult { State = PagePrintState.Failed, FailureStage = PrintFailureStage.SpoolerVerification, ErrorMessage = "Job did not appear in spooler or clear successfully" };
    }
}
