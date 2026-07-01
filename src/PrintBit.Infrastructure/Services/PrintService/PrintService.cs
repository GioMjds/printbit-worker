using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Shared.Configurations;
using System.Runtime.InteropServices;
using System.Text;

namespace PrintBit.Infrastructure.Services.PrintService;

[SupportedOSPlatform("windows")]
public class PrintService : IPrintService
{
    private static readonly SemaphoreSlim PrintLock = new(1, 1);

    protected const int VerificationTimeoutSeconds = 45;
    protected const int PostClearGuardWindowSeconds = 12;

    private readonly ILogger<PrintService> _logger;

    private readonly HardwareSettings _settings;

    private readonly IPrintRecoveryService _recoveryService;
    private readonly IPrintHealthCoordinator _printHealthCoordinator;

    public PrintService(
        ILogger<PrintService> logger,
        IOptions<HardwareSettings> options,
        IPrintRecoveryService recoveryService,
        IPrintHealthCoordinator printHealthCoordinator)
    {
        _logger = logger;
        _settings = options.Value;
        _recoveryService = recoveryService;
        _printHealthCoordinator = printHealthCoordinator;
    }

    public async Task<PrintJobResult> PrintAsync(
        PrintJobRequest request,
        CancellationToken cancellationToken = default)
    {
        await PrintLock.WaitAsync(cancellationToken);

        try
        {
            if (!PathExists(request.FilePath))
            {
                _logger.LogError(
                    "Print file does not exist: {file}",
                    request.FilePath);

                return PrintJobResult.Failed(
                    PrintFailureStage.Validation,
                    "Print file does not exist");
            }

            var sumatraPath = GetSumatraExecutablePath();

            if (!PathExists(sumatraPath))
            {
                _logger.LogError(
                    "SumatraPDF not found: {path}",
                    sumatraPath);

                return PrintJobResult.Failed(
                    PrintFailureStage.Validation,
                    "SumatraPDF executable not found");
            }

            _logger.LogInformation(
                "Starting print job | Printer: {printer} | File: {file}",
                request.PrinterName,
                request.FilePath);
            using var printAttempt = _printHealthCoordinator.BeginAttempt(
                request.PrinterName,
                Path.GetFileName(request.FilePath));

            var processResult = await ExecutePrintProcessAsync(
                request,
                sumatraPath,
                cancellationToken);

            if (!processResult.Success)
            {
                return processResult;
            }

            var verification = await VerifySpoolerLifecycleAsync(
                request.PrinterName,
                Path.GetFileName(request.FilePath),
                cancellationToken);

            if (!verification.Success)
            {
                _logger.LogError(
                    "Print verification failed | Stage={stage} | Message={message}",
                    verification.Stage,
                    verification.Message);

                return PrintJobResult.Failed(
                    verification.Stage,
                    verification.Message,
                    processResult.ExitCode,
                    verification.SpoolerJobId);
            }

            _logger.LogInformation(
                "Print completed and verified successfully");

            return new PrintJobResult
            {
                Success = true,
                Message = "Print completed and verified successfully",
                SumatraProcessSucceeded = true,
                VerificationSucceeded = true,
                FailureStage = PrintFailureStage.None,
                ExitCode = processResult.ExitCode,
                SpoolerJobId = verification.SpoolerJobId,
                SpoolerPrinterName = request.PrinterName
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unhandled print exception");

            await _recoveryService.RecoverAsync(cancellationToken);

            return PrintJobResult.Failed(
                PrintFailureStage.Unexpected,
                ex.Message);
        }
        finally
        {
            PrintLock.Release();
        }
    }

    protected virtual bool PathExists(
        string path)
    {
        return File.Exists(path);
    }

    protected virtual string GetSumatraExecutablePath()
    {
        return _settings.SumatraPath;
    }

    protected virtual async Task<PrintJobResult> ExecutePrintProcessAsync(
        PrintJobRequest request,
        string sumatraPath,
        CancellationToken cancellationToken)
    {
        using var process = BuildPrintProcess(
            sumatraPath,
            request);

        try
        {
            // Process.Start is synchronous and has no async wrapper. We accept
            // the brief blocking here because Sumatra cold-start (15-30s) and
            // the print timeout (120s) dominate the total time. A stuck Start
            // call (e.g. AV scan) would still be caught by the timeout below
            // because WaitForExitAsync reports exit once Start resolves.
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start print process");

            return PrintJobResult.Failed(
                PrintFailureStage.ProcessStart,
                $"Failed to start print process: {ex.Message}");
        }

        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(_settings.PrintTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(
                "Print timeout exceeded after {seconds} seconds",
                _settings.PrintTimeoutSeconds);

            KillHungProcess(process);

            await _recoveryService.RecoverAsync(cancellationToken);

            return PrintJobResult.Failed(
                PrintFailureStage.Timeout,
                "Print timeout exceeded");
        }

        var standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = await process.StandardError.ReadToEndAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            _logger.LogError(
                "Print process failed | ExitCode: {code} | Error: {error} | Output: {output}",
                process.ExitCode,
                standardError,
                standardOutput);

            return PrintJobResult.Failed(
                PrintFailureStage.ProcessExit,
                $"Print process exited with code {process.ExitCode}: {standardError}",
                process.ExitCode);
        }

        return new PrintJobResult
        {
            Success = true,
            Message = "Print process exited successfully",
            SumatraProcessSucceeded = true,
            VerificationSucceeded = false,
            FailureStage = PrintFailureStage.None,
            ExitCode = process.ExitCode
        };
    }

    protected virtual async Task<SpoolerVerificationResult> VerifySpoolerLifecycleAsync(
        string printerName,
        string expectedDocument,
        CancellationToken cancellationToken)
    {
        var result = await VerifySpoolerLifecycleInternalAsync(printerName, expectedDocument, cancellationToken);
        if (!result.Success)
        {
            CancelMatchingJobs(printerName, expectedDocument, result.SpoolerJobId);
        }
        return result;
    }

    private void CancelMatchingJobs(string printerName, string expectedDocument, string? spoolerJobId)
    {
        if (string.IsNullOrWhiteSpace(spoolerJobId))
        {
            _logger.LogWarning(
                "Skipping stuck print job cancellation because no spooler job ID was observed for printer '{printer}' and document '{doc}'",
                printerName,
                expectedDocument);
            return;
        }

        try
        {
            _logger.LogInformation(
                "Searching for stuck print jobs to cancel for printer '{printer}' and document '{doc}'",
                printerName,
                expectedDocument);

            // Pull JobId directly (uint) — avoids the localized "Name" format
            // ("Printer, JobId" varies by locale and silently mismatches on
            // non-US Windows).
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Document, JobStatus, JobId FROM Win32_PrintJob");

            var cancelledCount = 0;
            foreach (ManagementObject job in searcher.Get().Cast<ManagementObject>())
            {
                var jobName = job["Name"]?.ToString() ?? string.Empty;
                var document = job["Document"]?.ToString() ?? string.Empty;
                var jobId = Convert.ToUInt32(job["JobId"] ?? 0u);
                var spoolerJobIdInt = uint.TryParse(spoolerJobId, out var id) ? id : 0u;

                if (jobName.StartsWith(printerName, StringComparison.OrdinalIgnoreCase) &&
                    jobId == spoolerJobIdInt &&
                    document.Contains(expectedDocument, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Cancelling/Deleting stuck print job from Windows spooler: Name='{name}', Document='{doc}', JobId={jobId}, JobStatus='{status}'",
                        jobName,
                        document,
                        jobId,
                        job["JobStatus"]);

                    job.Delete();
                    cancelledCount++;
                }
            }

            if (cancelledCount > 0)
            {
                _logger.LogInformation("Successfully cancelled {count} stuck print job(s)", cancelledCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to cancel stuck print jobs for printer '{printer}' and document '{doc}'",
                printerName,
                expectedDocument);
        }
    }

    protected virtual async Task<SpoolerVerificationResult> VerifySpoolerLifecycleInternalAsync(
        string printerName,
        string expectedDocument,
        CancellationToken cancellationToken)
    {
        // WMI searchers are leased for the lifetime of this verification attempt.
        // Caching them avoids re-establishing the underlying IWbemServices
        // connection on every loop iteration (up to ~57 iterations: 45s in the
        // main loop + 12s post-clear guard). The PrintLock semaphore in
        // PrintAsync already serializes the whole pipeline, so the cached
        // searchers are not shared across attempts.
        using var printerSearcher = new ManagementObjectSearcher(
            $"SELECT DetectedErrorState FROM Win32_Printer WHERE Name = '{printerName}'");
        using var jobSearcher = new ManagementObjectSearcher(
            "SELECT Name, Document, StatusMask, JobStatus, JobId FROM Win32_PrintJob");

        var seenMatchingJob = false;
        string? lastSpoolerJobId = null;
        var deadline = DateTime.UtcNow.AddSeconds(VerificationTimeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_printHealthCoordinator.TryGetFatalHardwareError(printerName, out var signaledError))
            {
                _logger.LogError(
                    "Printer hardware error signaled by monitor: {message} (code {code})",
                    signaledError.Message,
                    signaledError.ErrorCode);
                return new SpoolerVerificationResult(
                    false,
                    PrintFailureStage.HardwareError,
                    $"Printer hardware error: {signaledError.Message} (code {signaledError.ErrorCode})",
                    lastSpoolerJobId);
            }

            // ── Printer-level hardware error check ──────────────────────
            //
            // NOTE: this WMI call and the GetMatchingJobs call below are
            // *not* a single WMI snapshot — there is a small window
            // (5–15 ms) where the spooler can change state between them.
            // We accept the race because:
            //   1. PrinterMonitorService runs in parallel and reports fatal
            //      hardware errors through IPrintHealthCoordinator
            //      (checked at the top of this loop), so the second
            //      error-channel is not lost.
            //   2. A true atomic WMI snapshot across two different
            //      classes (Win32_Printer, Win32_PrintJob) requires a
            //      ManagementScope + ManagementObjectCollection snapshot
            //      pattern that is fragile, error-prone, and
            //      platform-version dependent. The cost/benefit does not
            //      justify the complexity.
            var printerError = CheckPrinterErrorState(printerName, printerSearcher);

            if (printerError.HasError)
            {
                _logger.LogError(
                    "Printer hardware error during verification: {description} (code {code})",
                    printerError.Description,
                    printerError.ErrorCode);

                return new SpoolerVerificationResult(
                    false,
                    PrintFailureStage.HardwareError,
                    $"Printer hardware error: {printerError.Description} (code {printerError.ErrorCode})",
                    lastSpoolerJobId);
            }

            // ── Epson Status Monitor Popup Check ────────────────────────
            var epsonPopup = CheckEpsonStatusMonitorPopup();

            if (epsonPopup.HasPopup)
            {
                _logger.LogError(
                    "Epson Status Monitor popup detected ('{title}', PID {pid}). This indicates a hardware error (e.g., Paper Out) that the driver is hiding from the spooler.",
                    epsonPopup.WindowTitle,
                    epsonPopup.ProcessId);

                if (epsonPopup.ProcessId > 0)
                {
                    try
                    {
                        var popupProcess = Process.GetProcessById(epsonPopup.ProcessId);
                        popupProcess.Kill(true);
                        _logger.LogInformation("Killed Epson Status Monitor process {pid} to unblock UI.", epsonPopup.ProcessId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to kill Epson Status Monitor process {pid}", epsonPopup.ProcessId);
                    }
                }

                return new SpoolerVerificationResult(
                    false,
                    PrintFailureStage.HardwareError,
                    "Printer hardware error: Epson Status Monitor detected an issue (e.g., Paper Out)",
                    lastSpoolerJobId);
            }

            // ── Job-level status check ───────────────────────────────────
            var matchingJobs = GetMatchingJobs(
                printerName,
                expectedDocument,
                jobSearcher);

            if (matchingJobs.Count > 0)
            {
                seenMatchingJob = true;

                lastSpoolerJobId = matchingJobs[0].JobId > 0
                    ? matchingJobs[0].JobId.ToString()
                    : null;

                foreach (var job in matchingJobs)
                {
                    // StatusMask flags: ERROR = 0x2, PAPEROUT = 0x40
                    const uint errorFlag = 0x2;
                    const uint paperOutFlag = 0x40;

                    if ((job.StatusMask & (errorFlag | paperOutFlag)) != 0)
                    {
                        _logger.LogError(
                            "Print job has error flags | StatusMask=0x{mask:X} JobStatus={jobStatus}",
                            job.StatusMask,
                            job.JobStatus);

                        return new SpoolerVerificationResult(
                            false,
                            PrintFailureStage.HardwareError,
                            $"Print job error detected (StatusMask=0x{job.StatusMask:X}, JobStatus={job.JobStatus})",
                            lastSpoolerJobId);
                    }
                }

                _logger.LogInformation(
                    "Spooler verification observed matching print job(s): {count}",
                    matchingJobs.Count);
            }
            else if (seenMatchingJob)
            {
                _logger.LogInformation(
                    "Spooler job cleared; entering {seconds}s post-clear hardware guard window",
                    PostClearGuardWindowSeconds);

                for (int i = 0; i < PostClearGuardWindowSeconds; i++)
                {
                    // Check cancellation at the top of the iteration so the
                    // guard exits immediately when the caller cancels, rather
                    // than waiting up to a full second inside Task.Delay.
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(1000, cancellationToken);

                    if (_printHealthCoordinator.TryGetFatalHardwareError(printerName, out var delayedSignal))
                    {
                        _logger.LogError(
                            "Fatal hardware error signaled during post-clear guard: {message} (code {code})",
                            delayedSignal.Message,
                            delayedSignal.ErrorCode);
                        return new SpoolerVerificationResult(
                            false,
                            PrintFailureStage.HardwareError,
                            $"Printer hardware error: {delayedSignal.Message} (code {delayedSignal.ErrorCode})",
                            lastSpoolerJobId);
                    }
                    var finalPopup = CheckEpsonStatusMonitorPopup();
                    if (finalPopup.HasPopup)
                    {
                        _logger.LogError(
                            "Epson Status Monitor popup detected after job cleared ('{title}', PID {pid}).",
                            finalPopup.WindowTitle,
                            finalPopup.ProcessId);

                        if (finalPopup.ProcessId > 0)
                        {
                            try
                            {
                                var popupProcess = Process.GetProcessById(finalPopup.ProcessId);
                                popupProcess.Kill(true);
                                _logger.LogInformation("Killed Epson Status Monitor process {pid} to unblock UI.", finalPopup.ProcessId);
                            }
                            catch { }
                        }

                        return new SpoolerVerificationResult(
                            false,
                            PrintFailureStage.HardwareError,
                            "Printer hardware error: Epson Status Monitor detected an issue (e.g., Paper Out)",
                            lastSpoolerJobId);
                    }
                }

                // confirm the printer is still healthy before declaring success.
                var finalCheck = CheckPrinterErrorState(printerName, printerSearcher);

                if (finalCheck.HasError)
                {
                    _logger.LogError(
                        "Printer hardware error detected after job cleared: {description} (code {code})",
                        finalCheck.Description,
                        finalCheck.ErrorCode);

                    return new SpoolerVerificationResult(
                        false,
                        PrintFailureStage.HardwareError,
                        $"Printer hardware error: {finalCheck.Description} (code {finalCheck.ErrorCode})",
                        lastSpoolerJobId);
                }

                return new SpoolerVerificationResult(
                    true,
                    PrintFailureStage.None,
                    "Spooler lifecycle verified",
                    lastSpoolerJobId);
            }

            await Task.Delay(
                1000,
                cancellationToken);
        }

        if (!seenMatchingJob)
        {
            return new SpoolerVerificationResult(
                false,
                PrintFailureStage.SpoolerVerification,
                $"No spooler job observed for document '{expectedDocument}'",
                lastSpoolerJobId);
        }

        return new SpoolerVerificationResult(
            false,
            PrintFailureStage.SpoolerVerification,
            $"Spooler job for '{expectedDocument}' did not clear before timeout",
            lastSpoolerJobId);
    }

    /// <summary>
    /// Queries Win32_Printer.DetectedErrorState for the named printer.
    /// Codes ≥ 3 are treated as fatal hardware errors.
    /// </summary>
    protected virtual (bool HasError, int ErrorCode, string Description) CheckPrinterErrorState(
        string printerName,
        ManagementObjectSearcher searcher)
    {
        try
        {
            foreach (ManagementObject printer in TryQuery(searcher))
            {
                var raw = printer["DetectedErrorState"];

                if (raw is null)
                {
                    continue;
                }

                var errorCode = Convert.ToInt32(raw);

                // Codes 0 (Unknown), 1 (Other), 2 (No Error) are non-fatal.
                if (errorCode >= FatalErrorThreshold)
                {
                    return (true, errorCode, DetectedErrorStateDescription(errorCode));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to query printer error state — skipping hardware check");
        }

        return (false, 0, "No Error");
    }

    internal static Process BuildPrintProcess(
        string sumatraPath,
        PrintJobRequest request)
    {
        var settingsList = new List<string>
        {
            $"{Math.Max(1, request.Settings.Copies)}x",
            request.Settings.Color ? "color" : "monochrome"
        };

        if (!string.IsNullOrWhiteSpace(request.Settings.PageRange))
        {
            settingsList.Add(request.Settings.PageRange);
        }

        if (!string.IsNullOrWhiteSpace(request.Settings.Orientation))
        {
            settingsList.Add(request.Settings.Orientation);
        }

        var printSettingsArg = string.Join(",", settingsList);

        // Use ArgumentList (collection-based) instead of the legacy Arguments
        // string. The OS handles quoting/escaping for each element, eliminating
        // the shell-quoting attack surface that interpolation into a single
        // Arguments string would expose for printer names or file paths that
        // contain embedded quotes.
        var startInfo = new ProcessStartInfo
        {
            FileName = sumatraPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        startInfo.ArgumentList.Add("-print-to");
        startInfo.ArgumentList.Add(request.PrinterName);
        startInfo.ArgumentList.Add("-print-settings");
        startInfo.ArgumentList.Add(printSettingsArg);
        startInfo.ArgumentList.Add("-silent");
        startInfo.ArgumentList.Add(request.FilePath);

        return new Process { StartInfo = startInfo };
    }

    private void KillHungProcess(
        Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);

                _logger.LogWarning(
                    "Hung print process terminated");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to terminate hung print process");
        }
    }

    /// <summary>
    /// The minimum DetectedErrorState code treated as a fatal hardware error.
    /// Codes: 3=LowPaper, 4=NoPaper, 5=LowToner, 6=NoToner, 7=DoorOpen,
    ///        8=Jammed, 9=Offline, 10=ServiceRequested, 11=OutputBinFull.
    /// </summary>
    private const int FatalErrorThreshold = 3;

    private List<(string Name, string Document, uint StatusMask, string JobStatus, uint JobId)> GetMatchingJobs(
        string printerName,
        string expectedDocument,
        ManagementObjectSearcher searcher)
    {
        var matches = new List<(string Name, string Document, uint StatusMask, string JobStatus, uint JobId)>();

        foreach (ManagementObject job in TryQuery(searcher))
        {
            var jobName = job["Name"]?.ToString() ?? string.Empty;
            var document = job["Document"]?.ToString() ?? string.Empty;

            if (jobName.StartsWith(printerName, StringComparison.OrdinalIgnoreCase) &&
                document.Contains(expectedDocument, StringComparison.OrdinalIgnoreCase))
            {
                var statusMask = Convert.ToUInt32(job["StatusMask"] ?? 0u);
                var jobStatus = job["JobStatus"]?.ToString() ?? string.Empty;
                var jobId = Convert.ToUInt32(job["JobId"] ?? 0u);

                matches.Add((jobName, document, statusMask, jobStatus, jobId));
            }
        }

        return matches;
    }

    /// <summary>
    /// Runs the searcher's query and materializes the result. On a
    /// stale-connection <see cref="ManagementException"/> (e.g. spooler
    /// restart mid-print), returns an empty sequence so the verification
    /// loop can degrade gracefully instead of crashing. The cached searcher
    /// itself cannot be rebound in-place — System.Management constructs a
    /// new IWbemServices binding on the next <c>Get()</c> call only if the
    /// searcher was re-allocated, which we don't do here because the caller
    /// owns the searcher lifetime.
    /// </summary>
    private static IEnumerable<ManagementObject> TryQuery(ManagementObjectSearcher searcher)
    {
        try
        {
            return searcher.Get().Cast<ManagementObject>().ToList();
        }
        catch (ManagementException)
        {
            return Array.Empty<ManagementObject>();
        }
    }

    private static string DetectedErrorStateDescription(int code) => code switch
    {
        0 => "Unknown",
        1 => "Other",
        2 => "No Error",
        3 => "Low Paper",
        4 => "No Paper",
        5 => "Low Toner",
        6 => "No Toner",
        7 => "Door Open",
        8 => "Jammed",
        9 => "Offline",
        10 => "Service Requested",
        11 => "Output Bin Full",
        _ => $"Unknown Error ({code})"
    };

    // ── Epson Status Monitor Detection (P/Invoke) ──────────────────────────
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder strText, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static readonly string[] EpsonErrorKeywords = new[]
    {
        "paper out", "out of paper", "load paper", "no paper", "kehabisan kertas", "isi kertas",
        "paper jam", "jammed", "kertas macet", "jam",
        "service required", "service request",
        "ink out", "replace ink", "kehabisan tinta",
        "error", "fatal", "problem", "cannot print", "unable to print"
    };

    protected virtual (bool HasPopup, int ProcessId, string WindowTitle) CheckEpsonStatusMonitorPopup()
    {
        bool found = false;
        int targetPid = 0;
        string foundTitle = string.Empty;

        try
        {
            EnumWindows((hWnd, lParam) =>
            {
                if (IsWindowVisible(hWnd))
                {
                    var sb = new StringBuilder(256);
                    GetWindowText(hWnd, sb, 256);
                    string title = sb.ToString();

                    // Detect Epson Status Monitor window
                    if (title.StartsWith("EPSON Status Monitor 3", StringComparison.OrdinalIgnoreCase))
                    {
                        var windowTextBuilder = new StringBuilder();
                        windowTextBuilder.AppendLine(title);

                        EnumChildWindows(hWnd, (childHwnd, childLParam) =>
                        {
                            if (IsWindowVisible(childHwnd))
                            {
                                var childSb = new StringBuilder(256);
                                GetWindowText(childHwnd, childSb, 256);
                                if (childSb.Length > 0)
                                {
                                    windowTextBuilder.AppendLine(childSb.ToString());
                                }
                            }
                            return true;
                        }, IntPtr.Zero);

                        string fullContent = windowTextBuilder.ToString();

                        bool isActualError = false;
                        foreach (var keyword in EpsonErrorKeywords)
                        {
                            if (fullContent.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                            {
                                isActualError = true;
                                break;
                            }
                        }

                        if (isActualError)
                        {
                            GetWindowThreadProcessId(hWnd, out uint pid);
                            found = true;
                            targetPid = (int)pid;
                            foundTitle = title;
                            return false; // stop enumerating
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate windows for Epson Status Monitor check.");
        }

        return (found, targetPid, foundTitle);
    }
}

/// <summary>
/// Typed result of <see cref="PrintService.VerifySpoolerLifecycleInternalAsync"/>.
/// The <see cref="Stage"/> field is the dispatch key — callers must use it
/// (not message-string matching) to choose the failure stage. This replaces
/// the previous (bool, string, string?) tuple, which forced string-prefix
/// dispatch on the message and broke when the message copy changed.
/// </summary>
public record SpoolerVerificationResult(
    bool Success,
    PrintFailureStage Stage,
    string Message,
    string? SpoolerJobId);