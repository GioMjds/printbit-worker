using System.IO.Pipes;
using System.Management;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Shared.Configurations;

namespace PrintBit.Infrastructure.Windows.PrinterMonitoring;

[SupportedOSPlatform("windows")]
public class PrinterMonitorService : BackgroundService
{
    private readonly ILogger<PrinterMonitorService> _logger;
    private readonly HardwareSettings _hardwareSettings;
    private readonly IpcSettings _ipcSettings;

    // Track last known state to avoid flooding the pipe with repeat events
    private bool? _lastOfflineState = null;
    private string? _lastErrorState = null;

    public PrinterMonitorService(
        ILogger<PrinterMonitorService> logger,
        IOptions<HardwareSettings> hardwareOptions,
        IOptions<IpcSettings> ipcOptions)
    {
        _logger = logger;
        _hardwareSettings = hardwareOptions.Value;
        _ipcSettings = ipcOptions.Value;
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
            var status = printer["PrinterStatus"];
            var errorState = printer["DetectedErrorState"]?.ToString() ?? "0";

            _logger.LogInformation(
                "Printer status | Offline={offline} Status={status} Error={error}",
                isOffline, status, errorState);

            // ── Offline state change detection ──────────────────────────────
            if (_lastOfflineState != isOffline)
            {
                _lastOfflineState = isOffline;

                if (isOffline)
                {
                    _logger.LogWarning("Printer is OFFLINE — notifying Node.js");
                    await SendToReturnPipeAsync(new
                    {
                        type = "PrinterOffline",
                        printerName = _hardwareSettings.PrinterName,
                        message = "Printer is offline or unreachable. Check USB/network connection.",
                        timestampUtc = DateTime.UtcNow.ToString("o"),
                    }, stoppingToken);
                }
                else
                {
                    _logger.LogInformation("Printer is back ONLINE — notifying Node.js");
                    await SendToReturnPipeAsync(new
                    {
                        type = "PrinterOnline",
                        printerName = _hardwareSettings.PrinterName,
                        message = "Printer is back online.",
                        timestampUtc = DateTime.UtcNow.ToString("o"),
                    }, stoppingToken);
                }
            }

            // ── Error state change detection ─────────────────────────────────
            if (_lastErrorState != errorState)
            {
                _lastErrorState = errorState;

                if (errorState != "0")
                {
                    _logger.LogWarning("Printer error detected: {error}", errorState);
                    await SendToReturnPipeAsync(new
                    {
                        type = "PrinterOffline",
                        printerName = _hardwareSettings.PrinterName,
                        failureStage = "hardware_error",
                        message = $"Printer hardware error detected (code {errorState}). Check paper, ink, or connection.",
                        timestampUtc = DateTime.UtcNow.ToString("o"),
                    }, stoppingToken);
                }
            }
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

    /// <summary>
    /// Writes a newline-delimited JSON event to the worker return pipe.
    /// Node.js reads this in startWorkerReturnPipeServer() in worker-return-pipe.ts.
    /// </summary>
    private async Task SendToReturnPipeAsync(
        object payload,
        CancellationToken stoppingToken)
    {
        var pipeName = _ipcSettings.WorkerReturnPipeName; // "printbit-worker-events"

        try
        {
            using var pipeClient = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);

            // 3 second connect timeout — Node may not be ready yet
            await pipeClient.ConnectAsync(3_000, stoppingToken);

            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json + "\n"); // newline-delimited

            await pipeClient.WriteAsync(bytes, stoppingToken);
            await pipeClient.FlushAsync(stoppingToken);

            _logger.LogInformation(
                "[PIPE → Node] Sent {type} to {pipe}",
                payload.GetType().GetProperty("type")?.GetValue(payload),
                pipeName);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "[PIPE → Node] Connect timeout on {pipe} — Node.js may not be running",
                pipeName);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[PIPE → Node] Failed to send event to {pipe}",
                pipeName);
        }
    }
}