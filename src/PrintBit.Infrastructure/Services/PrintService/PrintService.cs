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
                var isHardwareError = verification.Message.StartsWith("Printer hardware error", StringComparison.Ordinal)
                    || verification.Message.StartsWith("Print job error detected", StringComparison.Ordinal);

                var failureStage = isHardwareError
                    ? PrintFailureStage.HardwareError
                    : PrintFailureStage.SpoolerVerification;

                _logger.LogError(
                    "Print verification failed | Stage={stage} | Message={message}",
                    failureStage,
                    verification.Message);

                return PrintJobResult.Failed(
                    failureStage,
                    verification.Message,
                    processResult.ExitCode);
            }

            _logger.LogInformation(
                "Print completed and verified successfully");

            return new PrintJobResult
            {
                Success = true,
                Message = "Print completed and verified successfully",
                ProcessSucceeded = true,
                VerificationSucceeded = true,
                FailureStage = PrintFailureStage.None,
                ExitCode = processResult.ExitCode
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
            ProcessSucceeded = true,
            VerificationSucceeded = false,
            FailureStage = PrintFailureStage.None,
            ExitCode = process.ExitCode
        };
    }

    protected virtual async Task<(bool Success, string Message)> VerifySpoolerLifecycleAsync(
        string printerName,
        string expectedDocument,
        CancellationToken cancellationToken)
    {
        var seenMatchingJob = false;
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
                return (false, $"Printer hardware error: {signaledError.Message} (code {signaledError.ErrorCode})");
            }

            // ── Printer-level hardware error check ──────────────────────
            var printerError = CheckPrinterErrorState(printerName);

            if (printerError.HasError)
            {
                _logger.LogError(
                    "Printer hardware error during verification: {description} (code {code})",
                    printerError.Description,
                    printerError.ErrorCode);

                return (false,
                    $"Printer hardware error: {printerError.Description} (code {printerError.ErrorCode})");
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

                return (false, "Printer hardware error: Epson Status Monitor detected an issue (e.g., Paper Out)");
            }

            // ── Job-level status check ───────────────────────────────────
            var matchingJobs = GetMatchingJobs(
                printerName,
                expectedDocument);

            if (matchingJobs.Count > 0)
            {
                seenMatchingJob = true;

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

                        return (false,
                            $"Print job error detected (StatusMask=0x{job.StatusMask:X}, JobStatus={job.JobStatus})");
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
                    await Task.Delay(1000, cancellationToken);

                    if (_printHealthCoordinator.TryGetFatalHardwareError(printerName, out var delayedSignal))
                    {
                        _logger.LogError(
                            "Fatal hardware error signaled during post-clear guard: {message} (code {code})",
                            delayedSignal.Message,
                            delayedSignal.ErrorCode);
                        return (false, $"Printer hardware error: {delayedSignal.Message} (code {delayedSignal.ErrorCode})");
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

                        return (false, "Printer hardware error: Epson Status Monitor detected an issue (e.g., Paper Out)");
                    }
                }

                // confirm the printer is still healthy before declaring success.
                var finalCheck = CheckPrinterErrorState(printerName);

                if (finalCheck.HasError)
                {
                    _logger.LogError(
                        "Printer hardware error detected after job cleared: {description} (code {code})",
                        finalCheck.Description,
                        finalCheck.ErrorCode);

                    return (false,
                        $"Printer hardware error: {finalCheck.Description} (code {finalCheck.ErrorCode})");
                }

                return (true, "Spooler lifecycle verified");
            }

            await Task.Delay(
                1000,
                cancellationToken);
        }

        if (!seenMatchingJob)
        {
            return (false, $"No spooler job observed for document '{expectedDocument}'");
        }

        return (false, $"Spooler job for '{expectedDocument}' did not clear before timeout");
    }

    /// <summary>
    /// Queries Win32_Printer.DetectedErrorState for the named printer.
    /// Codes ≥ 3 are treated as fatal hardware errors.
    /// </summary>
    protected virtual (bool HasError, int ErrorCode, string Description) CheckPrinterErrorState(
        string printerName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT DetectedErrorState FROM Win32_Printer WHERE Name = '{printerName}'");

            foreach (ManagementObject printer in searcher.Get())
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

        return new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = sumatraPath,
                Arguments =
                    $"-print-to \"{request.PrinterName}\" " +
                    $"-print-settings \"{printSettingsArg}\" " +
                    $"-silent " +
                    $"\"{request.FilePath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };
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

    private List<(string Name, string Document, uint StatusMask, string JobStatus)> GetMatchingJobs(
        string printerName,
        string expectedDocument)
    {
        var matches = new List<(string Name, string Document, uint StatusMask, string JobStatus)>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, Document, StatusMask, JobStatus FROM Win32_PrintJob");

        foreach (ManagementObject job in searcher.Get())
        {
            var jobName = job["Name"]?.ToString() ?? string.Empty;
            var document = job["Document"]?.ToString() ?? string.Empty;

            if (jobName.Contains(printerName, StringComparison.OrdinalIgnoreCase) &&
                document.Contains(expectedDocument, StringComparison.OrdinalIgnoreCase))
            {
                var statusMask = Convert.ToUInt32(job["StatusMask"] ?? 0u);
                var jobStatus = job["JobStatus"]?.ToString() ?? string.Empty;

                matches.Add((jobName, document, statusMask, jobStatus));
            }
        }

        return matches;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder strText, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

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

                    // Detect Epson Status Monitor popup
                    if (title.StartsWith("EPSON Status Monitor 3", StringComparison.OrdinalIgnoreCase))
                    {
                        GetWindowThreadProcessId(hWnd, out uint pid);
                        found = true;
                        targetPid = (int)pid;
                        foundTitle = title;
                        return false; // stop enumerating
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
