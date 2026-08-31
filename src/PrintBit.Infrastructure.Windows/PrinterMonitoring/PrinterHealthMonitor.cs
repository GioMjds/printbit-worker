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
    private readonly IWorkerEventPipeClient _eventPipe;
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
        "out of paper", "jam", "ink out", "no ink", "door open", "cover open", "offline", "error", "service"
    };

    public PrinterHealthMonitor(
        ILogger<PrinterHealthMonitor> logger,
        IOptions<HardwareSettings> hardwareOptions,
        IWorkerEventPipeClient eventPipe)
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
        var wmiHealthy = IsWmiHealthy(printerName, out _);
        var (hasPopup, _, _, _) = CheckEpsonStatusMonitorPopup(printerName);

        return wsHealthy && wmiHealthy && !hasPopup;
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

        if (!IsWmiHealthy(printerName, out var wmiDesc))
        {
            errorCode = 3;
            errorMessage = wmiDesc;
            return true;
        }

        var (hasPopup, pid, title, content) = CheckEpsonStatusMonitorPopup(printerName);
        if (hasPopup)
        {
            errorCode = 99;
            errorMessage = $"Epson Popup: {content}";
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
            KillEpsonProcesses();
            await RestartSpoolerAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovery failed");
        }
    }

    private void KillEpsonProcesses()
    {
        string[] epsonExes = { "E_YATIYRE", "E_YARNYRE", "E_S11RPB", "E_YBCSYRE" };
        foreach (var name in epsonExes)
        {
            KillProcess(name);
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

                    if (jobName.StartsWith(printerName, StringComparison.OrdinalIgnoreCase) &&
                        document.Contains(documentName, StringComparison.OrdinalIgnoreCase))
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

    protected virtual bool TryReadMonitorStatus(
        string printerName,
        out bool isOffline,
        out int detectedErrorState,
        out int extendedPrinterStatus)
    {
        isOffline = false;
        detectedErrorState = 0;
        extendedPrinterStatus = 0;

        try
        {
            var escapedPrinterName = printerName.Replace("'", "''");
            using var searcher = new ManagementObjectSearcher(
                $"SELECT DetectedErrorState, ExtendedPrinterStatus, WorkOffline FROM Win32_Printer WHERE Name = '{escapedPrinterName}'");

            using var results = searcher.Get();
            foreach (ManagementObject printer in results.Cast<ManagementObject>())
            {
                try
                {
                    isOffline = printer["WorkOffline"] is true;
                    extendedPrinterStatus = Convert.ToInt32(printer["ExtendedPrinterStatus"] ?? 0);
                    if (extendedPrinterStatus is 7 or 11)
                    {
                        isOffline = true;
                    }

                    detectedErrorState = Convert.ToInt32(printer["DetectedErrorState"] ?? 0);
                    return true;
                }
                finally
                {
                    printer.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read printer status for {printerName}", printerName);
        }

        return false;
    }

    protected virtual async Task MonitorPrinterAsync(CancellationToken stoppingToken)
    {
        var foundPrinter = TryReadMonitorStatus(
            _hardwareSettings.PrinterName,
            out var isOffline,
            out var detectedErrorState,
            out var extendedPrinterStatus);

        if (foundPrinter)
        {
            var isFatalWmi = IsFatalDetectedErrorState(detectedErrorState);
            var isFatalExtendedStatus =
                IsFatalExtendedPrinterStatus(extendedPrinterStatus) && extendedPrinterStatus is not (7 or 11);

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
                if (isFatalWmi || isFatalExtendedStatus)
                {
                    _fatalErrorCode = isFatalWmi
                        ? detectedErrorState
                        : extendedPrinterStatus;
                    _fatalErrorMessage = isFatalWmi
                        ? DetectedErrorStateDescription(detectedErrorState)
                        : ExtendedPrinterStatusDescription(extendedPrinterStatus);

                    var stateIdentifier = $"{detectedErrorState}_{extendedPrinterStatus}";
                    if (_lastErrorState != stateIdentifier)
                    {
                        _lastErrorState = stateIdentifier;
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
        else
        {
            if (_lastOfflineState != true)
            {
                _lastOfflineState = true;
                _pendingEvent = new WorkerPrintEvent
                {
                    Type = WorkerPrintEventType.PrinterOffline,
                    PrinterName = _hardwareSettings.PrinterName,
                    Message = $"Printer queue '{_hardwareSettings.PrinterName}' not found"
                };
            }
            lock (_lock)
            {
                _fatalErrorCode = 1;
                _fatalErrorMessage = $"Printer queue '{_hardwareSettings.PrinterName}' not found";
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
        bool foundPrinter = false;
        try
        {
            var escapedPrinterName = printerName.Replace("'", "''");
            using var searcher = new ManagementObjectSearcher(
                $"SELECT DetectedErrorState, ExtendedPrinterStatus, WorkOffline FROM Win32_Printer WHERE Name = '{escapedPrinterName}'");
            
            using var results = searcher.Get();
            foreach (ManagementObject printer in results.Cast<ManagementObject>())
            {
                foundPrinter = true;
                try
                {
                    if (printer["WorkOffline"] is true)
                    {
                        wmiDesc = "Offline";
                        return false;
                    }
                    var extStatus = Convert.ToInt32(printer["ExtendedPrinterStatus"] ?? 0);
                    if (IsFatalExtendedPrinterStatus(extStatus))
                    {
                        wmiDesc = $"Extended status {extStatus} ({ExtendedPrinterStatusDescription(extStatus)})";
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

            if (!foundPrinter)
            {
                wmiDesc = $"Printer queue '{printerName}' not found";
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WMI query failed for {printerName}", printerName);
            wmiDesc = $"WMI query failed: {ex.Message}";
            return false;
        }
        return true;
    }

    private static bool IsFatalDetectedErrorState(int code) => code switch
    {
        4 => true,  // No Paper
        6 => true,  // No Toner
        7 => true,  // Door Open
        8 => true,  // Jammed
        9 => true,  // Offline
        10 => true, // Service Requested
        11 => true, // Output Bin Full
        _ => false
    };

    private static bool IsFatalExtendedPrinterStatus(int status) => status switch
    {
        6 => true,  // Stopped Printing
        7 => true,  // Offline
        9 => true,  // Error
        11 => true, // Not Available
        _ => false
    };

    private static string ExtendedPrinterStatusDescription(int status) => status switch
    {
        1 => "Other",
        2 => "Unknown",
        3 => "Idle",
        4 => "Printing",
        5 => "Warmup",
        6 => "Stopped Printing",
        7 => "Offline",
        8 => "Paused",
        9 => "Error",
        10 => "Busy",
        11 => "Not Available",
        12 => "Waiting",
        13 => "Processing",
        14 => "Initialization",
        15 => "Power Save",
        16 => "Pending Deletion",
        17 => "I/O Active",
        18 => "Manual Feed",
        _ => $"Status ({status})"
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

    private (bool HasPopup, int ProcessId, string WindowTitle, string Content) CheckEpsonStatusMonitorPopup(string printerName)
    {
        bool found = false;
        int targetPid = 0;
        string foundTitle = string.Empty;
        string foundContent = string.Empty;

        try
        {
            _ = EnumWindows((hWnd, lParam) =>
            {
                if (IsWindowVisible(hWnd))
                {
                    var sb = new StringBuilder(256);
                    _ = GetWindowText(hWnd, sb, 256);
                    string title = sb.ToString();

                    if (title.StartsWith("EPSON Status Monitor 3", StringComparison.OrdinalIgnoreCase))
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
                            var printerParts = printerName.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                                .Where(part => !string.Equals(part, "Series", StringComparison.OrdinalIgnoreCase) &&
                                               !string.Equals(part, "Printer", StringComparison.OrdinalIgnoreCase) &&
                                               !string.Equals(part, "Epson", StringComparison.OrdinalIgnoreCase))
                                .ToList();

                            bool nameMatches = printerParts.Count > 0
                                ? printerParts.Any(part => fullContent.Contains(part, StringComparison.OrdinalIgnoreCase))
                                : fullContent.Contains(printerName, StringComparison.OrdinalIgnoreCase);

                            if (nameMatches)
                            {
                                _ = GetWindowThreadProcessId(hWnd, out uint pid);
                                found = true;
                                targetPid = (int)pid;
                                foundTitle = title;
                                
                                // Clean up the content string for Kiosk display (remove newlines and generic text)
                                var cleanContent = string.Join(" | ", fullContent
                                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                    .Where(l => !l.Contains("EPSON Status Monitor") && !l.Contains("Buy EPSON Ink")));
                                foundContent = cleanContent.Length > 0 ? cleanContent : "Epson popup error";
                                return false;
                            }
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
        return (found, targetPid, foundTitle, foundContent);
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
