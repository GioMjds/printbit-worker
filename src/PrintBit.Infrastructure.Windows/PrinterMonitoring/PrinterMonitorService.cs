using System.Management;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Shared.Configurations;

namespace PrintBit.Infrastructure.Windows.PrinterMonitoring;

[SupportedOSPlatform("windows")]
public class PrinterMonitorService : BackgroundService
{
    private readonly ILogger<PrinterMonitorService> _logger;

    private readonly HardwareSettings _settings;

    public PrinterMonitorService(
        ILogger<PrinterMonitorService> logger,
        IOptions<HardwareSettings> options)
    {
        _logger = logger;
        _settings = options.Value;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Printer monitor started for {printer}",
            _settings.PrinterName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                MonitorPrinterStatus();
                MonitorPrintJobs();
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Printer monitoring failed");
            }

            await Task.Delay(
                2000,
                stoppingToken);
        }
    }

    private void MonitorPrinterStatus()
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT * FROM Win32_Printer WHERE Name = '{_settings.PrinterName}'");

        foreach (ManagementObject printer in searcher.Get())
        {
            var isOffline = printer["WorkOffline"];
            var status = printer["PrinterStatus"];
            var errorState = printer["DetectedErrorState"];

            _logger.LogInformation(
                "Printer status | Offline={offline} Status={status} Error={error}",
                isOffline,
                status,
                errorState);

            if (isOffline is true)
            {
                _logger.LogWarning("Printer is OFFLINE");
            }

            if (errorState != null && errorState.ToString() != "0")
            {
                _logger.LogWarning(
                    "Printer error detected: {error}",
                    errorState);
            }
        }
    }

    private void MonitorPrintJobs()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT * FROM Win32_PrintJob");

        foreach (ManagementObject job in searcher.Get())
        {
            var name = job["Name"];
            var status = job["JobStatus"];
            var document = job["Document"];

            _logger.LogInformation(
                "Print job | Name={name} Document={doc} Status={status}",
                name,
                document,
                status);
        }
    }
}