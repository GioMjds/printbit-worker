using System.Management;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Infrastructure.IPC;
using PrintBit.Shared.Configurations;

namespace PrintBit.Infrastructure.Windows.PrinterMonitoring;

[SupportedOSPlatform("windows")]
public class PrinterMonitorService : BackgroundService
{
    // 3-second cold-start connect timeout. The default 500ms is too short
    // when Node.js is still starting up alongside this worker.
    private const int NodeConnectTimeoutMs = 3_000;

    private readonly ILogger<PrinterMonitorService> _logger;
    private readonly HardwareSettings _hardwareSettings;
    private readonly WorkerEventPipeClient _eventPipe;

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
        WorkerEventPipeClient eventPipe)
    {
        _logger = logger;
        _hardwareSettings = hardwareOptions.Value;
        _eventPipe = eventPipe;
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

        foreach (ManagementObject printer in searcher.Get())
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

            // ── Error state change detection ─────────────────────────────────
            if (_lastErrorState != errorState && errorState != "0")
            {
                _lastErrorState = errorState;
                _pendingEvent = new WorkerPrintEvent
                {
                    Type = WorkerPrintEventType.PrinterError,
                    PrinterName = _hardwareSettings.PrinterName,
                    FailureStage = "hardware_error",
                    Message = $"Printer hardware error detected (code {errorState}). Check paper, ink, or connection.",
                };

                _logger.LogWarning("Printer error detected: {error}", errorState);
            }
        }

        // Best-effort drain of the latest pending event. If Node.js is
        // unreachable right now, the next poll cycle will retry.
        if (_pendingEvent is not null
            && await _eventPipe.SendAsync(_pendingEvent, stoppingToken, NodeConnectTimeoutMs))
        {
            _pendingEvent = null;
        }
    }

    private void MonitorPrintJobs()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT * FROM Win32_PrintJob");

        foreach (ManagementObject job in searcher.Get())
        {
            _logger.LogInformation(
                "Print job | Name={name} Document={doc} Status={status}",
                job["Name"], job["Document"], job["JobStatus"]);
        }
    }
}
