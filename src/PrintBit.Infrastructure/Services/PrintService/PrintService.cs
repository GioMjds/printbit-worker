using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Shared.Configurations;

namespace PrintBit.Infrastructure.Services.PrintService;

[SupportedOSPlatform("windows")]
public class PrintService : IPrintService
{
    private static readonly SemaphoreSlim PrintLock = new(1, 1);

    protected const int VerificationTimeoutSeconds = 45;

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
            if (!PathExists(request.FilePath))
            {
                _logger.LogError(
                    "Print file does not exist: {file}",
                    request.FilePath);

                return PrintJobResult.Failed(
                    PrintFailureStage.Validation,
                    "Print file does not exist");
            }

            var sumatraPath = GetSumatraExecutablePath();

            if (!PathExists(sumatraPath))
            {
                _logger.LogError(
                    "SumatraPDF not found: {path}",
                    sumatraPath);

                return PrintJobResult.Failed(
                    PrintFailureStage.Validation,
                    "SumatraPDF executable not found");
            }

            _logger.LogInformation(
                "Starting print job | Printer: {printer} | File: {file}",
                request.PrinterName,
                request.FilePath);

            var processResult = await ExecutePrintProcessAsync(
                request,
                sumatraPath,
                cancellationToken);

            if (!processResult.Success)
            {
                return processResult;
            }

            var verification = await VerifySpoolerLifecycleAsync(
                request.PrinterName,
                Path.GetFileName(request.FilePath),
                cancellationToken);

            if (!verification.Success)
            {
                _logger.LogError(
                    "Print verification failed: {message}",
                    verification.Message);

                return PrintJobResult.Failed(
                    PrintFailureStage.SpoolerVerification,
                    verification.Message,
                    processResult.ExitCode);
            }

            _logger.LogInformation(
                "Print completed and verified successfully");

            return new PrintJobResult
            {
                Success = true,
                Message = "Print completed and verified successfully",
                ProcessSucceeded = true,
                VerificationSucceeded = true,
                FailureStage = PrintFailureStage.None,
                ExitCode = processResult.ExitCode
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unhandled print exception");

            await _recoveryService.RecoverAsync(cancellationToken);

            return PrintJobResult.Failed(
                PrintFailureStage.Unexpected,
                ex.Message);
        }
        finally
        {
            PrintLock.Release();
        }
    }

    protected virtual bool PathExists(
        string path)
    {
        return File.Exists(path);
    }

    protected virtual string GetSumatraExecutablePath()
    {
        return @"C:\Users\printbit\bin\SumatraPDF.exe";
    }

    protected virtual async Task<PrintJobResult> ExecutePrintProcessAsync(
        PrintJobRequest request,
        string sumatraPath,
        CancellationToken cancellationToken)
    {
        using var process = BuildPrintProcess(
            sumatraPath,
            request);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start print process");

            return PrintJobResult.Failed(
                PrintFailureStage.ProcessStart,
                $"Failed to start print process: {ex.Message}");
        }

        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(_settings.PrintTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(
                "Print timeout exceeded after {seconds} seconds",
                _settings.PrintTimeoutSeconds);

            KillHungProcess(process);

            await _recoveryService.RecoverAsync(cancellationToken);

            return PrintJobResult.Failed(
                PrintFailureStage.Timeout,
                "Print timeout exceeded");
        }

        var standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = await process.StandardError.ReadToEndAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            _logger.LogError(
                "Print process failed | ExitCode: {code} | Error: {error} | Output: {output}",
                process.ExitCode,
                standardError,
                standardOutput);

            return PrintJobResult.Failed(
                PrintFailureStage.ProcessExit,
                $"Print process exited with code {process.ExitCode}: {standardError}",
                process.ExitCode);
        }

        return new PrintJobResult
        {
            Success = true,
            Message = "Print process exited successfully",
            ProcessSucceeded = true,
            VerificationSucceeded = false,
            FailureStage = PrintFailureStage.None,
            ExitCode = process.ExitCode
        };
    }

    protected virtual async Task<(bool Success, string Message)> VerifySpoolerLifecycleAsync(
        string printerName,
        string expectedDocument,
        CancellationToken cancellationToken)
    {
        var seenMatchingJob = false;
        var deadline = DateTime.UtcNow.AddSeconds(VerificationTimeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var matchingJobs = GetMatchingJobs(
                printerName,
                expectedDocument);

            if (matchingJobs.Count > 0)
            {
                seenMatchingJob = true;

                _logger.LogInformation(
                    "Spooler verification observed matching print job(s): {count}",
                    matchingJobs.Count);
            }
            else if (seenMatchingJob)
            {
                return (true, "Spooler lifecycle verified");
            }

            await Task.Delay(
                1000,
                cancellationToken);
        }

        if (!seenMatchingJob)
        {
            return (false, $"No spooler job observed for document '{expectedDocument}'");
        }

        return (false, $"Spooler job for '{expectedDocument}' did not clear before timeout");
    }

    internal static Process BuildPrintProcess(
        string sumatraPath,
        PrintJobRequest request)
    {
        var settingsList = new List<string>
        {
            $"{Math.Max(1, request.Settings.Copies)}x",
            request.Settings.Color ? "color" : "monochrome"
        };

        if (!string.IsNullOrWhiteSpace(request.Settings.PageRange))
        {
            settingsList.Add(request.Settings.PageRange);
        }

        if (!string.IsNullOrWhiteSpace(request.Settings.Orientation))
        {
            settingsList.Add(request.Settings.Orientation);
        }

        var printSettingsArg = string.Join(",", settingsList);

        return new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = sumatraPath,
                Arguments =
                    $"-print-to \"{request.PrinterName}\" " +
                    $"-print-settings \"{printSettingsArg}\" " +
                    $"-silent " +
                    $"\"{request.FilePath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };
    }

    private void KillHungProcess(
        Process process)
    {
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
    }

    private List<(string Name, string Document)> GetMatchingJobs(
        string printerName,
        string expectedDocument)
    {
        var matches = new List<(string Name, string Document)>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, Document FROM Win32_PrintJob");

        foreach (ManagementObject job in searcher.Get())
        {
            var jobName = job["Name"]?.ToString() ?? string.Empty;
            var document = job["Document"]?.ToString() ?? string.Empty;

            if (jobName.Contains(printerName, StringComparison.OrdinalIgnoreCase) &&
                document.Contains(expectedDocument, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add((jobName, document));
            }
        }

        return matches;
    }
}
