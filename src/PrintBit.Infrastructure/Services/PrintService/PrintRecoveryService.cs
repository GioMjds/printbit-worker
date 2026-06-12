using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PrintBit.Infrastructure.Services.PrintService;

public class PrintRecoveryService : IPrintRecoveryService
{
    private readonly ILogger<PrintRecoveryService> _logger;

    public PrintRecoveryService(
        ILogger<PrintRecoveryService> logger)
    {
        _logger = logger;
    }

    public async Task RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogWarning(
                "Starting printer recovery");

            KillProcess("SumatraPDF");

            KillProcess("PDFtoPrinter");

            KillProcess("E_YARNYRE"); // Epson Status Monitor 3 (L5290)

            await RestartSpoolerAsync();

            _logger.LogInformation(
                "Printer recovery completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Printer recovery failed");
        }
    }

    private void KillProcess(string processName)
    {
        var processes =
            Process.GetProcessesByName(processName);

        foreach (var process in processes)
        {
            try
            {
                process.Kill(true);

                _logger.LogWarning(
                    "Killed process: {process}",
                    processName);
            }
            catch
            {
            }
        }
    }

    private async Task RestartSpoolerAsync()
    {
        var stopProcess = Process.Start(
            new ProcessStartInfo
            {
                FileName = "cmd.exe",

                Arguments = "/c net stop spooler",

                CreateNoWindow = true,

                UseShellExecute = false
            });

        if (stopProcess != null)
        {
            await stopProcess.WaitForExitAsync();
        }

        await Task.Delay(2000);

        var startProcess = Process.Start(
            new ProcessStartInfo
            {
                FileName = "cmd.exe",

                Arguments = "/c net start spooler",

                CreateNoWindow = true,

                UseShellExecute = false
            });

        if (startProcess != null)
        {
            await startProcess.WaitForExitAsync();
        }
    }
}