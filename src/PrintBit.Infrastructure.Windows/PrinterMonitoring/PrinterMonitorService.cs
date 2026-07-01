using System.Management;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;

namespace PrintBit.Infrastructure.Windows.PrinterMonitoring;

[SupportedOSPlatform("windows")]
public class PrinterMonitorService : BackgroundService
{
    private readonly ILogger<PrinterMonitorService> _logger;
    private readonly HardwareSettings _hardwareSettings;
    private readonly IpcSettings _ipcSettings;
    private readonly WorkerEventPipeClient _eventPipe;
    private readonly IPrintHealthCoordinator _printHealthCoordinator;

    // Track last known state to avoid flooding the pipe with repeat events.
    private bool? _lastOfflineState = null;
    private string? _lastErrorState = null;

    // Holds the most recent printer event that has not yet been
    // acknowledged by the Node.js listener. Survives transient
    // TimeoutException / UnauthorizedAccessException / IOException.
    private WorkerPrintEvent? _pendingEvent;

    public PrinterMonitorService(
        ILogger<PrinterMonitorService> logger,
        IOptions<HardwareSettings> hardwareOptions,
        IOptions<IpcSettings> ipcOptions,
        WorkerEventPipeClient eventPipe,
        IPrintHealthCoordinator printHealthCoordinator)
    {
        _logger = logger;
        _hardwareSettings = hardwareOptions.Value;
        _ipcSettings = ipcOptions.Value;
        _eventPipe = eventPipe;
        _printHealthCoordinator = printHealthCoordinator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Printer monitor started for {printer}",
            _hardwareSettings.PrinterName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MonitorPrinterStatusAsync(stoppingToken);
                MonitorPrintJobs();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Printer monitoring failed");
            }

            await Task.Delay(2000, stoppingToken);
        }
    }

    private async Task MonitorPrinterStatusAsync(CancellationToken stoppingToken)
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT * FROM Win32_Printer WHERE Name = '{_hardwareSettings.PrinterName}'");

        foreach (ManagementObject printer in searcher.Get().Cast<ManagementObject>())
        {
            var isOffline = printer["WorkOffline"] is true;
            var status = printer["Status"];
            var errorState = printer["DetectedErrorState"]?.ToString() ?? "0";

            _logger.LogInformation(
                "Printer status | Offline={offline} Status={status} Error={error}",
                isOffline, status, errorState);

            // ── Offline state change detection ──────────────────────────────
            if (_lastOfflineState != isOffline)
            {
                _lastOfflineState = isOffline;
                _pendingEvent = new WorkerPrintEvent
                {
                    Type = isOffline
                        ? WorkerPrintEventType.PrinterOffline
                        : WorkerPrintEventType.PrinterOnline,
                    PrinterName = _hardwareSettings.PrinterName,
                    Message = isOffline
                        ? "Printer is offline or unreachable. Check USB/network connection."
                        : "Printer is back online.",
                };

                _logger.Log(isOffline ? LogLevel.Warning : LogLevel.Information,
                    "Printer is {state} — notifying Node.js",
                    isOffline ? "OFFLINE" : "back ONLINE");
            }

            if (errorState == "0")
            {
                _lastErrorState = null;
            }

            // ── Error state change detection (fatal only: >= 3) ─────────────
            var parsedErrorCode = int.TryParse(errorState, out var code) ? code : 0;
            var isFatalError = parsedErrorCode >= 3;

            if (isFatalError)
            {
                _printHealthCoordinator.ReportFatalHardwareError(
                    _hardwareSettings.PrinterName,
                    parsedErrorCode,
                    $"{DetectedErrorStateDescription(parsedErrorCode)}");

                if (_lastErrorState != errorState)
                {
                    _lastErrorState = errorState;
                    _pendingEvent = new WorkerPrintEvent
                    {
                        Type = WorkerPrintEventType.PrinterError,
                        PrinterName = _hardwareSettings.PrinterName,
                        FailureStage = "hardware_error",
                        Message = $"Printer hardware error detected ({DetectedErrorStateDescription(parsedErrorCode)}, code {errorState}). Check paper, ink, or connection.",
                    };

                    _logger.LogWarning(
                        "Fatal printer hardware error detected: {description} (code {error})",
                        DetectedErrorStateDescription(parsedErrorCode),
                        errorState);
                }
            }
        }

        // Best-effort drain of the latest pending event. If Node.js is
        // unreachable right now, the next poll cycle will retry.
        if (_pendingEvent is not null
            && await _eventPipe.SendAsync(_pendingEvent, stoppingToken))
        {
            _pendingEvent = null;
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

    private void MonitorPrintJobs()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT * FROM Win32_PrintJob");

        foreach (ManagementObject job in searcher.Get().Cast<ManagementObject>())
        {
            _logger.LogInformation(
                "Print job | Name={name} Document={doc} Status={status}",
                job["Name"], job["Document"], job["JobStatus"]);
        }
    }
}
