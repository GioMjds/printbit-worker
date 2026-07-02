using System.Collections.Generic;
using System.Management;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;
using PrintBit.Shared.Printing;

namespace PrintBit.Infrastructure.Windows.PrinterMonitoring;

[SupportedOSPlatform("windows")]
public class PrinterMonitorService : BackgroundService
{
    private readonly ILogger<PrinterMonitorService> _logger;
    private readonly HardwareSettings _hardwareSettings;
    private readonly IpcSettings _ipcSettings;
    private readonly WorkerEventPipeClient _eventPipe;
    private readonly IPrintHealthCoordinator _printHealthCoordinator;

    // Track last-seen (PagesPrinted, TotalPages) per spooler job id so we only
    // emit a PrintProgress event when something has actually changed. The WMI
    // poll runs every 2 s, so emitting unconditionally would flood the named
    // pipe (one event every 2 s × N tracked jobs, even when no progress is
    // being made). We also key by correlation key (parsed from the document
    // filename) so the Node side can match the event to a lifecycle record
    // without depending on the spooler's transient JobId.
    private readonly Dictionary<uint, (int Pages, int Total)> _lastSeenPages = new();

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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Printer monitoring failed");
            }

            try
            {
                await MonitorPrintJobsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Print job monitoring failed");
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

    private async Task MonitorPrintJobsAsync(CancellationToken stoppingToken)
    {
        using var searcher = new ManagementObjectSearcher(
            // Mirrors the SELECT used in PrintService.cs:326 but adds the
            // page-count columns we need for progress reporting.
            "SELECT Name, Document, StatusMask, JobStatus, JobId, PagesPrinted, TotalPages FROM Win32_PrintJob");

        foreach (ManagementObject job in searcher.Get().Cast<ManagementObject>())
        {
            var jobName = job["Name"]?.ToString() ?? string.Empty;
            var document = job["Document"]?.ToString() ?? string.Empty;
            var jobStatus = job["JobStatus"]?.ToString() ?? string.Empty;
            var jobId = Convert.ToUInt32(job["JobId"] ?? 0u);

            _logger.LogInformation(
                "Print job | Name={name} Document={doc} Status={status}",
                jobName, document, jobStatus);

            // Only emit progress for jobs that belong to the configured
            // printer. PrintQueueWatcherService also enforces this on its
            // side via Sumatra's job-naming, but the WMI query here is
            // printer-wide so we filter defensively.
            if (!jobName.StartsWith(_hardwareSettings.PrinterName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Parse the correlation key out of the document filename. The
            // worker writes files as
            //   {transactionId}_{spoolerCorrelationKey}_{timestamp}.pdf
            // (see PrintBit.Shared.Printing.PrintJobFileName and
            // worker-handoff.ts on the Node side). If the document doesn't
            // match that shape (e.g. it was dispatched by some other
            // process), skip — Node has no lifecycle record to attach this
            // progress to and emitting a half-formed event would just
            // confuse the receiver.
            var (transactionId, spoolerCorrelationKey) =
                PrintJobFileName.TryParseCorrelation(document);
            if (transactionId is null || spoolerCorrelationKey is null)
            {
                continue;
            }

            // PagesPrinted and TotalPages are uint on WMI; coerce via
            // Convert.ToInt32 which returns 0 on null. Filter 0/0 — that
            // just means WMI hasn't latched onto the job yet, and emitting
            // it would clobber Node's "I have no progress" state with a
            // misleading "0 of 0".
            var pagesPrinted = Convert.ToInt32(job["PagesPrinted"] ?? 0);
            var totalPages = Convert.ToInt32(job["TotalPages"] ?? 0);
            if (pagesPrinted == 0 && totalPages == 0)
            {
                continue;
            }

            // Dedup: only emit when the (pages, total) tuple changes. The
            // spec calls for "whenever the PagesPrinted field changes for
            // a tracked job", so a job that has finished or is between
            // pages is silent. This keeps the pipe from being flooded
            // while still emitting one event per page boundary.
            if (_lastSeenPages.TryGetValue(jobId, out var prev)
                && prev.Pages == pagesPrinted
                && prev.Total == totalPages)
            {
                continue;
            }
            _lastSeenPages[jobId] = (pagesPrinted, totalPages);

            // Replace any other pending event with this progress update —
            // progress is the freshest signal we have. We deliberately
            // overwrite a PrinterOffline/PrinterOnline pending event here
            // because a paused-on-paper-out job that just resumed will
            // emit both kinds of events in quick succession and Node only
            // needs the freshest one.
            _pendingEvent = new WorkerPrintEvent
            {
                Type = WorkerPrintEventType.PrintProgress,
                TransactionId = transactionId,
                SpoolerCorrelationKey = spoolerCorrelationKey,
                SpoolerJobId = jobId.ToString(),
                FileName = document,
                PrinterName = _hardwareSettings.PrinterName,
                PagesPrinted = pagesPrinted,
                TotalPages = totalPages,
                // Message is intentionally null — Node has dedicated
                // fields for the page counts; a free-text message here
                // would just be noise in the admin log.
            };

            _logger.LogInformation(
                "Print progress | JobId={jobId} Document={doc} {pages}/{total}",
                jobId, document, pagesPrinted, totalPages);
        }

        // Best-effort drain of the latest pending event (same pattern as
        // MonitorPrinterStatusAsync). If Node is unreachable right now, the
        // next poll cycle will retry. We await here so the loop in
        // ExecuteAsync doesn't fire the next 2s timer while a send is
        // still in flight.
        if (_pendingEvent is not null
            && await _eventPipe.SendAsync(_pendingEvent, stoppingToken))
        {
            _pendingEvent = null;
        }
    }
}
