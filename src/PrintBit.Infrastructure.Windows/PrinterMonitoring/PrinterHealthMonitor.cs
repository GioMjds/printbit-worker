using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;

namespace PrintBit.Infrastructure.Windows.PrinterMonitoring;

[SupportedOSPlatform("windows")]
public class PrinterHealthMonitor : BackgroundService, IPrinterHealthMonitor
{
    private readonly ILogger<PrinterHealthMonitor> _logger;
    private readonly HardwareSettings _hardwareSettings;
    private readonly WorkerEventPipeClient _eventPipe;
    private readonly object _lock = new();

    private bool? _lastOfflineState = null;
    private string? _lastErrorState = null;
    private WorkerPrintEvent? _pendingEvent;
    
    private int _fatalErrorCode = 0;
    private string _fatalErrorMessage = string.Empty;

    // Win32 APIs for Epson Status Monitor Popup checking
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private static readonly string[] EpsonErrorKeywords = 
    {
        "out of paper", "paper out", "paper is out", "no paper", "paper empty", "rear paper",
        "feed paper", "load paper", "set paper", "load plain", "load a4", "empty",
        "w-01", "w-02", "w-03", "w-04", "w-05", "jam", "ink out", "no ink",
        "door open", "cover open", "offline", "error", "service"
    };

    public PrinterHealthMonitor(
        ILogger<PrinterHealthMonitor> logger,
        IOptions<HardwareSettings> hardwareOptions,
        WorkerEventPipeClient eventPipe)
    {
        _logger = logger;
        _hardwareSettings = hardwareOptions.Value;
        _eventPipe = eventPipe;
    }

    public virtual bool IsHealthy(string printerName, out int winSpoolStatus, out string winSpoolDesc)
    {
        var winSpoolOk = WinSpoolApi.GetPrinterStatus(printerName, out var status, out winSpoolDesc);
        winSpoolStatus = (int)status;
        var wsHealthy = winSpoolOk && ((status & WinSpoolApi.FATAL_STATUS_MASK) == 0);
        var wmiHealthy = IsWmiHealthy(printerName, out var wmiDesc);
        var (hasPopup, popupPid, popupTitle) = CheckEpsonStatusMonitorPopup(printerName);

        bool healthy = wsHealthy && wmiHealthy && !hasPopup;
        if (!healthy)
        {
            _logger.LogWarning("IsHealthy FALSE for '{printer}': wsHealthy={ws} (status 0x{status:X} - {winSpoolDesc}), wmiHealthy={wmi} ({wmiDesc}), hasPopup={popup} (PID {popupPid} - '{popupTitle}')",
                printerName, wsHealthy, status, winSpoolDesc, wmiHealthy, wmiDesc, hasPopup, popupPid, popupTitle);
        }

        return healthy;
    }

    public bool HasFatalHardwareError(string printerName, out int errorCode, out string errorMessage)
    {
        if (string.Equals(printerName, _hardwareSettings.PrinterName, StringComparison.OrdinalIgnoreCase))
        {
            lock (_lock)
            {
                if (_fatalErrorCode != 0)
                {
                    errorCode = _fatalErrorCode;
                    errorMessage = _fatalErrorMessage;
                    return true;
                }
            }
        }

        var winSpoolOk = WinSpoolApi.GetPrinterStatus(printerName, out var winSpoolStatus, out var winSpoolDesc);
        if (winSpoolOk && (winSpoolStatus & WinSpoolApi.FATAL_STATUS_MASK) != 0)
        {
            errorCode = (int)winSpoolStatus;
            errorMessage = $"WinSpool reports: {winSpoolDesc}";
            return true;
        }

        var (jobExists, jobMask, jobStatusStr, _, _, _) = QueryJobStatus(printerName, string.Empty);
        if (jobExists && ((jobMask & (0x2 | 0x40 | 0x20)) != 0 || jobStatusStr.Contains("Error", StringComparison.OrdinalIgnoreCase) || jobStatusStr.Contains("Paper", StringComparison.OrdinalIgnoreCase)))
        {
            errorCode = (int)jobMask;
            errorMessage = $"Spooler job error: {jobStatusStr} (0x{jobMask:X})";
            return true;
        }
        if (!IsWmiHealthy(printerName, out var wmiDesc))
        {
            errorCode = 3;
            errorMessage = wmiDesc;
            return true;
        }

        var (hasPopup, pid, title) = CheckEpsonStatusMonitorPopup(printerName);
        if (hasPopup)
        {
            errorCode = 99;
            errorMessage = $"Epson Popup: {title} (PID {pid})";
            return true;
        }

        errorCode = 0;
        errorMessage = string.Empty;
        return false;
    }

    public async Task<bool> WaitForPrinterHealthyAsync(
        string printerName,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Waiting for printer '{printer}' (timeout: {timeout}s)", printerName, timeoutSeconds);
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline || timeoutSeconds == 0)
        {
            if (cancellationToken.IsCancellationRequested) return false;

            if (IsHealthy(printerName, out var status, out var desc))
            {
                _logger.LogInformation("Printer '{printer}' is healthy.", printerName);
                return true;
            }

            _logger.LogWarning("Printer unhealthy: wsStatus=0x{status:X} ({desc}). Nudging...", status, desc);
            _ = WinSpoolApi.SetPrinterStatusReset(printerName);
            _ = WinSpoolApi.NudgePrinter(printerName);

            if (timeoutSeconds == 0) return false; // immediate check

            await Task.Delay(2000, cancellationToken);
        }
        return false;
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogWarning("Executing printer recovery spooler restarts...");
            KillProcess("SumatraPDF");
            KillProcess("E_YARNYRE");
            await RestartSpoolerAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovery failed");
        }
    }

    public (bool JobExists, uint StatusMask, string JobStatus, int PagesPrinted, int TotalPages, string? JobId) QueryJobStatus(
        string printerName,
        string documentName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Document, StatusMask, JobStatus, JobId, PagesPrinted, TotalPages FROM Win32_PrintJob");

            using var results = searcher.Get();
            foreach (ManagementObject job in results.Cast<ManagementObject>())
            {
                try
                {
                    var jobName = job["Name"]?.ToString() ?? string.Empty;
                    var document = job["Document"]?.ToString() ?? string.Empty;

                    // Match any print job assigned to this printer name
                    if (jobName.StartsWith(printerName, StringComparison.OrdinalIgnoreCase))
                    {
                        var mask = Convert.ToUInt32(job["StatusMask"] ?? 0u);
                        var status = job["JobStatus"]?.ToString() ?? string.Empty;
                        var printed = Convert.ToInt32(job["PagesPrinted"] ?? 0);
                        var total = Convert.ToInt32(job["TotalPages"] ?? 0);
                        var jobId = Convert.ToUInt32(job["JobId"] ?? 0u).ToString();
                        return (true, mask, status, printed, total, jobId);
                    }
                }
                finally
                {
                    job.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query print job status");
        }
        return (false, 0, string.Empty, 0, 0, null);
    }

    public void CancelMatchingJobs(string printerName, string documentName, string? spoolerJobId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Document, JobStatus, JobId FROM Win32_PrintJob");

            using var results = searcher.Get();
            foreach (ManagementObject job in results.Cast<ManagementObject>())
            {
                try
                {
                    var jobName = job["Name"]?.ToString() ?? string.Empty;
                    var document = job["Document"]?.ToString() ?? string.Empty;
                    var jobId = Convert.ToUInt32(job["JobId"] ?? 0u).ToString();

                    if (jobName.StartsWith(printerName, StringComparison.OrdinalIgnoreCase) &&
                        (document.Contains(documentName, StringComparison.OrdinalIgnoreCase) || jobId == spoolerJobId))
                    {
                        _logger.LogWarning("Deleting stuck print job {jobId}: {doc}", jobId, document);
                        job.Delete();
                    }
                }
                finally
                {
                    job.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel print job");
        }
    }

    public void PauseMatchingJobs(string printerName, string documentName, string? spoolerJobId) =>
        ControlMatchingJobs(printerName, documentName, spoolerJobId, WinSpoolApi.JOB_CONTROL_PAUSE, "Pausing");

    public void ResumeMatchingJobs(string printerName, string documentName, string? spoolerJobId) =>
        ControlMatchingJobs(printerName, documentName, spoolerJobId, WinSpoolApi.JOB_CONTROL_RESUME, "Resuming");

    // Shared pause/resume driver. Mirrors CancelMatchingJobs' WMI job matching
    // (by printer-prefixed Name + document substring, or explicit JobId) but
    // issues a WinSpool SetJob control command instead of deleting the job.
    // Best-effort: swallows all failures so the between-pages dispatch hold
    // remains the guaranteed mechanism.
    private void ControlMatchingJobs(string printerName, string documentName, string? spoolerJobId, int command, string verb)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Document, JobStatus, JobId FROM Win32_PrintJob");

            using var results = searcher.Get();
            foreach (ManagementObject job in results.Cast<ManagementObject>())
            {
                try
                {
                    var jobName = job["Name"]?.ToString() ?? string.Empty;
                    var document = job["Document"]?.ToString() ?? string.Empty;
                    var jobIdRaw = Convert.ToUInt32(job["JobId"] ?? 0u);
                    var jobId = jobIdRaw.ToString();

                    if (jobName.StartsWith(printerName, StringComparison.OrdinalIgnoreCase) &&
                        (document.Contains(documentName, StringComparison.OrdinalIgnoreCase) || jobId == spoolerJobId))
                    {
                        _logger.LogInformation("{verb} spooler job {jobId}: {doc}", verb, jobId, document);
                        _ = WinSpoolApi.ControlJob(printerName, (int)jobIdRaw, command);
                    }
                }
                finally
                {
                    job.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to {verb} print job (best-effort)", verb.ToLowerInvariant());
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Unified Printer Monitor loop started for {printer}", _hardwareSettings.PrinterName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MonitorPrinterAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in health monitoring loop");
            }
            await Task.Delay(2000, stoppingToken);
        }
    }

    private async Task MonitorPrinterAsync(CancellationToken stoppingToken)
    {
        var escapedPrinterName = _hardwareSettings.PrinterName.Replace("'", "''");
        using var searcher = new ManagementObjectSearcher(
            $"SELECT DetectedErrorState, WorkOffline FROM Win32_Printer WHERE Name = '{escapedPrinterName}'");

        using var results = searcher.Get();
        foreach (ManagementObject printer in results.Cast<ManagementObject>())
        {
            try
            {
                var isOffline = printer["WorkOffline"] is true;
                var errorStateRaw = printer["DetectedErrorState"]?.ToString() ?? "0";
                var errorCode = int.TryParse(errorStateRaw, out var c) ? c : 0;

                if (_lastOfflineState != isOffline)
                {
                    _lastOfflineState = isOffline;
                    _pendingEvent = new WorkerPrintEvent
                    {
                        Type = isOffline ? WorkerPrintEventType.PrinterOffline : WorkerPrintEventType.PrinterOnline,
                        PrinterName = _hardwareSettings.PrinterName,
                        Message = isOffline ? "Printer offline" : "Printer back online"
                    };
                }

                lock (_lock)
                {
                    if (IsFatalDetectedErrorState(errorCode))
                    {
                        _fatalErrorCode = errorCode;
                        _fatalErrorMessage = DetectedErrorStateDescription(errorCode);

                        if (_lastErrorState != errorStateRaw)
                        {
                            _lastErrorState = errorStateRaw;
                            _pendingEvent = new WorkerPrintEvent
                            {
                                Type = WorkerPrintEventType.PrinterError,
                                PrinterName = _hardwareSettings.PrinterName,
                                FailureStage = "hardware_error",
                                Message = $"Printer error ({_fatalErrorMessage})"
                            };
                        }
                    }
                    else
                    {
                        _fatalErrorCode = 0;
                        _fatalErrorMessage = string.Empty;
                        _lastErrorState = null;
                    }
                }
            }
            finally
            {
                printer.Dispose();
            }
        }

        if (_pendingEvent is not null && await _eventPipe.SendAsync(_pendingEvent, stoppingToken))
        {
            _pendingEvent = null;
        }
    }

    private bool IsWmiHealthy(string printerName, out string wmiDesc)
    {
        wmiDesc = "OK";
        try
        {
            var escapedPrinterName = printerName.Replace("'", "''");
            using var searcher = new ManagementObjectSearcher(
                $"SELECT PrinterState, PrinterStatus, DetectedErrorState, ExtendedPrinterStatus, WorkOffline FROM Win32_Printer WHERE Name = '{escapedPrinterName}'");
            
            using var results = searcher.Get();
            foreach (ManagementObject printer in results.Cast<ManagementObject>())
            {
                try
                {
                    if (printer["WorkOffline"] is true)
                    {
                        wmiDesc = "Offline";
                        return false;
                    }

                    var pState = Convert.ToUInt32(printer["PrinterState"] ?? 0u);
                    if ((pState & WinSpoolApi.FATAL_STATUS_MASK) != 0)
                    {
                        wmiDesc = $"PrinterState 0x{pState:X} ({WinSpoolApi.GetStatusDescription(pState)})";
                        return false;
                    }

                    var err = Convert.ToInt32(printer["DetectedErrorState"] ?? 0);
                    if (IsFatalDetectedErrorState(err))
                    {
                        wmiDesc = $"Error {err} ({DetectedErrorStateDescription(err)})";
                        return false;
                    }
                }
                finally
                {
                    printer.Dispose();
                }
            }
        }
        catch
        {
            // best effort
        }

        return true;
    }

    private static uint GetRegistryPrinterStatus(string printerName)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Print\Printers\{printerName}");
            if (key != null)
            {
                var val = key.GetValue("Status");
                if (val != null)
                {
                    return Convert.ToUInt32(val);
                }
            }
        }
        catch
        {
            // best effort
        }
        return 0;
    }

    private static bool IsFatalDetectedErrorState(int code) => code switch
    {
        3 => false, // Low Paper (Warning only, non-fatal)
        5 => false, // Low Toner (Warning only, non-fatal)
        4 or 6 or 7 or 8 or 9 or 10 or 11 => true,
        _ => false
    };

    private static string DetectedErrorStateDescription(int code) => code switch
    {
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

    private (bool HasPopup, int ProcessId, string WindowTitle) CheckEpsonStatusMonitorPopup(string printerName)
    {
        bool found = false;
        int targetPid = 0;
        string foundTitle = string.Empty;

        try
        {
            _ = EnumWindows((hWnd, lParam) =>
            {
                if (IsWindowVisible(hWnd))
                {
                    var sb = new StringBuilder(256);
                    _ = GetWindowText(hWnd, sb, 256);
                    string title = sb.ToString();

                    if (title.Contains("Status Monitor", StringComparison.OrdinalIgnoreCase) ||
                        title.Contains("EPSON", StringComparison.OrdinalIgnoreCase))
                    {
                        var textBuilder = new StringBuilder();
                        textBuilder.AppendLine(title);

                        _ = EnumChildWindows(hWnd, (childHwnd, childLParam) =>
                        {
                            if (IsWindowVisible(childHwnd))
                            {
                                var childSb = new StringBuilder(256);
                                GetWindowText(childHwnd, childSb, 256);
                                if (childSb.Length > 0)
                                {
                                    textBuilder.AppendLine(childSb.ToString());
                                }
                            }
                            return true;
                        }, IntPtr.Zero);

                        string fullContent = textBuilder.ToString();
                        bool isError = EpsonErrorKeywords.Any(k => fullContent.Contains(k, StringComparison.OrdinalIgnoreCase));

                        if (isError)
                        {
                            _ = GetWindowThreadProcessId(hWnd, out uint pid);
                            found = true;
                            targetPid = (int)pid;
                            foundTitle = title;
                            return false;
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // ignored
        }
        return (found, targetPid, foundTitle);
    }

    public async Task DismissAndResetAsync(string printerName, CancellationToken cancellationToken)
    {
        _logger.LogInformation("[Resume] Dismissing EPSON Status Monitor popup and resetting printer error state.");

        // 1. Target detected popup process ID directly if visible
        var (hasPopup, popupPid, popupTitle) = CheckEpsonStatusMonitorPopup(printerName);
        if (hasPopup && popupPid > 0)
        {
            _logger.LogInformation("[Resume] Found popup process PID {pid} ('{title}'). Terminating...", popupPid, popupTitle);
            KillProcessByPid(popupPid);
        }

        // 2. Kill known EPSON Status Monitor host processes so popup no longer blocks
        KillProcess("E_YARNYRE");   // EPSON Status Monitor host process
        KillProcess("EPMNSNT");     // alternate EPSON monitor process name
        KillProcess("E_S10IC2");    // another EPSON variant
        KillProcess("E_F10IC2");
        await Task.Delay(500, cancellationToken); // give the OS time to clean up

        // 2. Reset WinSpool error flags.
        _ = WinSpoolApi.SetPrinterStatusReset(printerName);
        _ = WinSpoolApi.NudgePrinter(printerName);

        // 3. Clear the cached fatal error so HasFatalHardwareError returns false
        //    immediately on the next call, before the background loop runs again.
        lock (_lock)
        {
            _fatalErrorCode = 0;
            _fatalErrorMessage = string.Empty;
        }
        _lastErrorState = null;

        _logger.LogInformation("[Resume] Printer reset complete. Polling for healthy state...");

        // 4. Poll IsHealthy for up to 3 seconds so EnterPreFlightPauseAsync
        //    immediately sees a clean printer rather than still-stale state.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsHealthy(printerName, out _, out _))
            {
                _logger.LogInformation("[Resume] Printer is healthy after reset.");
                return;
            }
            await Task.Delay(500, cancellationToken);
        }

        _logger.LogWarning("[Resume] Printer not yet healthy after 3s reset wait — patience loop will continue polling.");
    }

    private void KillProcessByPid(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            using (p)
            {
                p.Kill(true);
            }
        }
        catch { }
    }

    private void KillProcess(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes)
        {
            using (p)
            {
                try { p.Kill(true); } catch { }
            }
        }
    }

    private async Task RestartSpoolerAsync()
    {
        using var stop = Process.Start(new ProcessStartInfo 
        { 
            FileName = "cmd.exe", 
            Arguments = "/c net stop spooler", 
            CreateNoWindow = true, 
            UseShellExecute = false 
        });
        if (stop is not null)
        {
            await stop.WaitForExitAsync();
            if (stop.ExitCode != 0)
            {
                _logger.LogWarning("Failed to stop spooler service (exit code: {ExitCode}). Make sure the service is running with Administrative privileges.", stop.ExitCode);
            }
        }
        await Task.Delay(2000);
        using var start = Process.Start(new ProcessStartInfo 
        { 
            FileName = "cmd.exe", 
            Arguments = "/c net start spooler", 
            CreateNoWindow = true, 
            UseShellExecute = false 
        });
        if (start is not null)
        {
            await start.WaitForExitAsync();
            if (start.ExitCode != 0)
            {
                _logger.LogWarning("Failed to start spooler service (exit code: {ExitCode}). Make sure the service is running with Administrative privileges.", start.ExitCode);
            }
        }
    }
}
