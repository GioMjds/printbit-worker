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
        Action<string> onPaused,
        Action onResumed,
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
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
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
            return await VerifySpoolerPageLifecycleAsync(printerName, Path.GetFileName(filePath), onPaused, onResumed, cancellationToken);
        }
        finally
        {
            PrintLock.Release();
        }
    }

    private async Task<PagePrintResult> VerifySpoolerPageLifecycleAsync(
        string printerName,
        string documentName,
        Action<string> onPaused,
        Action onResumed,
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
                    
                    // StatusMask: 0x2 (ERROR), 0x40 (PAPEROUT)
                    bool jobHasError = (statusMask & (0x2 | 0x40)) != 0;
                    bool fatalMonitorError = _healthMonitor.HasFatalHardwareError(printerName, out _, out var fatalMsg);
                    bool isDeleting = (statusMask & (0x4 | 0x100)) != 0 || jobStatus.Contains("Deleting", StringComparison.OrdinalIgnoreCase);

                    // If seen in a normal progress state (no error, not deleting), it is considered active/printing
                    bool isNormalProgress = !jobHasError && !isDeleting;

                    if (isNormalProgress)
                    {
                        observedActive = true;
                    }

                    if (jobHasError || fatalMonitorError)
                    {
                        if (!inPatienceMode)
                        {
                            inPatienceMode = true;
                            var errorMsg = fatalMonitorError ? fatalMsg : $"Spooler error status: {jobStatus} (0x{statusMask:X})";
                            onPaused(errorMsg);
                        }
                    }
                    else
                    {
                        if (inPatienceMode)
                        {
                            inPatienceMode = false;
                            onResumed();
                            deadline = DateTime.UtcNow.AddSeconds(45); // reset normal verification timeout
                        }
                    }
                }
                else
                {
                    // Job cleared / not found
                    if (observedActive)
                    {
                        // If we previously saw it, and it completed successfully, it transitions through printed
                        // We run a post-clear hardware guard window
                        _logger.LogInformation("Job cleared from spooler; running post-clear hardware guard window");
                        await Task.Delay(TimeSpan.FromSeconds(_settings.PostClearGuardDelaySeconds), cancellationToken);

                        if (_healthMonitor.HasFatalHardwareError(printerName, out var code, out var msg))
                        {
                            return new PagePrintResult { State = PagePrintState.Failed, FailureStage = PrintFailureStage.HardwareError, ErrorMessage = $"Post-clear hardware error code {code}: {msg}" };
                        }
                        return new PagePrintResult { State = PagePrintState.Completed };
                    }
                    
                    if (lastSpoolerJobId != null)
                    {
                        // Job was previously seen in the spooler, but vanished without ever being observed active/printing.
                        // This represents a cancellation (e.g. user pressed Stop ✖ which deleted it from the queue).
                        _logger.LogWarning("Spooler job {jobId} vanished without being observed active; treating as cancelled", lastSpoolerJobId);
                        return new PagePrintResult { State = PagePrintState.Cancelled, ErrorMessage = "Spooler job vanished without printing; likely cancelled by user" };
                    }
                    
                    // Job was never observed in the spooler queue at all
                    if (_healthMonitor.IsHealthy(printerName, out _, out _))
                    {
                        // Treated as printed fast (i.e. printed and cleared before the first poll)
                        return new PagePrintResult { State = PagePrintState.Completed };
                    }
                }

                await Task.Delay(2000, cancellationToken);
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
