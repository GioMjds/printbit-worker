using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Shared.Configurations;

namespace PrintBit.Infrastructure.Services.PrintService;

public class PrintService : IPrintService
{
    private static readonly SemaphoreSlim PrintLock =
        new(1, 1);

    private readonly ILogger<PrintService> _logger;

    private readonly HardwareSettings _settings;

    private readonly IPrintRecoveryService _recoveryService;

    public PrintService(
        ILogger<PrintService> logger,
        IOptions<HardwareSettings> options,
        IPrintRecoveryService recoveryService)
    {
        _logger = logger;

        _settings = options.Value;

        _recoveryService = recoveryService;
    }

    public async Task<PrintJobResult> PrintAsync(
        PrintJobRequest request,
        CancellationToken cancellationToken = default)
    {
        await PrintLock.WaitAsync(cancellationToken);

        try
        {
            if (!File.Exists(request.FilePath))
            {
                _logger.LogError(
                    "Print file does not exist: {file}",
                    request.FilePath);

                return new PrintJobResult
                {
                    Success = false,
                    Message = "Print file does not exist"
                };
            }

            var sumatraPath = @"C:\Users\printbit\bin\SumatraPDF.exe";

            if (!File.Exists(sumatraPath))
            {
                _logger.LogError(
                    "SumatraPDF not found: {path}",
                    sumatraPath);

                return new PrintJobResult
                {
                    Success = false,
                    Message = "SumatraPDF executable not found"
                };
            }

            _logger.LogInformation(
                "Starting print job | Printer: {printer} | File: {file}",
                request.PrinterName,
                request.FilePath);

            using var process = new Process();

            process.StartInfo = new ProcessStartInfo
            {
                FileName = sumatraPath,

                Arguments =
                    $"-print-to \"{request.PrinterName}\" " +
                    $"-silent " +
                    $"\"{request.FilePath}\"",

                CreateNoWindow = true,

                UseShellExecute = false,

                RedirectStandardError = true,

                RedirectStandardOutput = true
            };

            process.Start();

            using var timeoutCts =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(
                        _settings.PrintTimeoutSeconds));

            using var linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(
                    linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogError(
                    "Print timeout exceeded after {seconds} seconds",
                    _settings.PrintTimeoutSeconds);

                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);

                        _logger.LogWarning(
                            "Hung print process terminated");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to terminate hung print process");
                }

                await _recoveryService.RecoverAsync(
                    cancellationToken);

                return new PrintJobResult
                {
                    Success = false,
                    Message = "Print timeout exceeded"
                };
            }

            var standardOutput =
                await process.StandardOutput.ReadToEndAsync(
                    cancellationToken);

            var standardError =
                await process.StandardError.ReadToEndAsync(
                    cancellationToken);

            if (process.ExitCode != 0)
            {
                _logger.LogError(
                    "Print process failed | ExitCode: {code} | Error: {error}",
                    process.ExitCode,
                    standardError);

                return new PrintJobResult
                {
                    Success = false,
                    Message =
                        $"Print failed | ExitCode={process.ExitCode} | Error={standardError}"
                };
            }

            _logger.LogInformation(
                "Print completed successfully");

            return new PrintJobResult
            {
                Success = true,
                Message = "Print completed successfully"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unhandled print exception");

            await _recoveryService.RecoverAsync(
                cancellationToken);

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