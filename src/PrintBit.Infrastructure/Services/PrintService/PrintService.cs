using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PrintBit.Infrastructure.Services.PrintService;

public class PrintService : IPrintService
{
    private static readonly SemaphoreSlim PrintLock = new(1, 1);

    private readonly ILogger<PrintService> _logger;

    public PrintService(
        ILogger<PrintService> logger)
    {
        _logger = logger;
    }

    public async Task<PrintJobResult> PrintAsync(
        PrintJobRequest request,
        CancellationToken cancellationToken = default)
    {
        await PrintLock.WaitAsync(cancellationToken);

        try
        {
            _logger.LogInformation(
                "Starting print job for file: {file}",
                request.FilePath);

            var process = new Process();

            process.StartInfo = new ProcessStartInfo
            {
                FileName = "SumatraPDF.exe",

                Arguments =
                    $"-print-to \"{request.PrinterName}\" " +
                    $"\"{request.FilePath}\"",

                CreateNoWindow = true,

                UseShellExecute = false
            };

            process.Start();

            await process.WaitForExitAsync(
                cancellationToken);

            if (process.ExitCode != 0)
            {
                return new PrintJobResult
                {
                    Success = false,
                    Message =
                        $"Print process failed with exit code {process.ExitCode}"
                };
            }

            _logger.LogInformation(
                "Print completed successfully");

            return new PrintJobResult
            {
                Success = true,
                Message = "Print completed"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Print failed");

            return new PrintJobResult
            {
                Success = false,
                Message = ex.Message
            };
        }
        finally
        {
            PrintLock.Release();
        }
    }
}