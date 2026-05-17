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
        if (request is null) 
            throw new ArgumentNullException(nameof(request));

        if (string.IsNullOrWhiteSpace(request.FilePath))
            return new PrintJobResult { Success = false, Message = "File path is required." };

        if (string.IsNullOrWhiteSpace(request.PrinterName))
            return new PrintJobResult { Success = false, Message = "PrinterName is required" };

        if (request.Copies <= 0)
            return new PrintJobResult { Success = false, Message = "Copies must be greater than zero" };

        await PrintLock.WaitAsync(cancellationToken);

        try
        {
            _logger.LogInformation(
                "Starting print job for file: {file}",
                request.FilePath);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "SumatraPDF.exe",
                    Arguments =
                        $"-print-to \"{request.PrinterName}\" " +
                        $"-print-settings \"{request.Copies}\" " +
                        $"\"{request.FilePath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                }
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(2)); // Set a timeout for the print job

            process.Start();

            await process.WaitForExitAsync(timeoutCts.Token);

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