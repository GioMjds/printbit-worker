using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Shared.Configurations;
using PrintBit.Shared.Printing;

namespace PrintBit.Infrastructure.Services.PrintService;

public sealed class DocumentPrinter : IDocumentPrinter
{
    private const uint JobErrorMask =
        0x00000002 | // Error
        0x00000020 | // Offline
        0x00000040 | // Paper out
        0x00000200 | // Blocked device queue
        0x00000400;  // User intervention required

    private static readonly SemaphoreSlim PrintLock = new(1, 1);
    private readonly ILogger<DocumentPrinter> _logger;
    private readonly HardwareSettings _settings;
    private readonly IPrinterHealthMonitor _healthMonitor;

    public DocumentPrinter(
        ILogger<DocumentPrinter> logger,
        IOptions<HardwareSettings> options,
        IPrinterHealthMonitor healthMonitor)
    {
        _logger = logger;
        _settings = options.Value;
        _healthMonitor = healthMonitor;
    }

    public async Task<DocumentPrintResult> PrintDocumentAsync(
        string filePath,
        string printerName,
        int copyNumber,
        IReadOnlyList<int> pages,
        PrintJobSettings settings,
        Func<int, int, Task> onProgress,
        Func<string, Task> onPaused,
        Func<Task> onResumed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(onProgress);
        ArgumentNullException.ThrowIfNull(onPaused);
        ArgumentNullException.ThrowIfNull(onResumed);

        if (pages.Count == 0)
        {
            return Failed(PrintFailureStage.Validation, "No pages selected for printing", 0);
        }

        await PrintLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(filePath))
            {
                return Failed(PrintFailureStage.Validation, "PDF file not found", pages.Count);
            }

            if (!File.Exists(_settings.SumatraPath))
            {
                return Failed(PrintFailureStage.Validation, "SumatraPDF executable not found", pages.Count);
            }

            ApplyPrintQuality(printerName, settings.Quality);

            _logger.LogInformation(
                "Dispatching whole PDF copy {copyNumber} from {filePath} to {printerName} ({pageCount} selected pages)",
                copyNumber,
                filePath,
                printerName,
                pages.Count);

            using var process = BuildPrintProcess(
                _settings.SumatraPath,
                filePath,
                printerName,
                pages,
                settings);

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                return Failed(PrintFailureStage.ProcessStart, ex.Message, pages.Count);
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
                try { process.Kill(true); } catch { }
                await _healthMonitor.RecoverAsync(cancellationToken);
                return Failed(PrintFailureStage.Timeout, "Sumatra process timeout", pages.Count);
            }

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(cancellationToken);
                return Failed(PrintFailureStage.ProcessExit, error, pages.Count);
            }

            return await VerifySpoolerDocumentLifecycleAsync(
                printerName,
                Path.GetFileName(filePath),
                pages.Count,
                onProgress,
                onPaused,
                onResumed,
                cancellationToken);
        }
        finally
        {
            PrintLock.Release();
        }
    }

    internal static Process BuildPrintProcess(
        string sumatraPath,
        string filePath,
        string printerName,
        IReadOnlyList<int> pages,
        PrintJobSettings settings)
    {
        var printSettings = new List<string>
        {
            "1x",
            settings.Color ? "color" : "monochrome",
            FormatPageSelection(pages)
        };

        if (settings.Orientation is "portrait" or "landscape")
        {
            printSettings.Add(settings.Orientation);
        }

        printSettings.Add("collate");

        var startInfo = new ProcessStartInfo
        {
            FileName = sumatraPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-print-to");
        startInfo.ArgumentList.Add(printerName);
        startInfo.ArgumentList.Add("-print-settings");
        startInfo.ArgumentList.Add(string.Join(',', printSettings));
        startInfo.ArgumentList.Add("-silent");
        startInfo.ArgumentList.Add(filePath);

        return new Process { StartInfo = startInfo };
    }

    private async Task<DocumentPrintResult> VerifySpoolerDocumentLifecycleAsync(
        string printerName,
        string documentName,
        int expectedPages,
        Func<int, int, Task> onProgress,
        Func<string, Task> onPaused,
        Func<Task> onResumed,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        var patienceDeadline = DateTime.UtcNow.AddMinutes(_settings.PauseTimeoutMinutes);
        var inPatienceMode = false;
        var observedActive = false;
        var maxPagesPrinted = 0;
        var lastTotalPages = 0;
        string? lastSpoolerJobId = null;
        string? activeErrorMessage = null;

        try
        {
            while (DateTime.UtcNow < deadline ||
                   (inPatienceMode && DateTime.UtcNow < patienceDeadline))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (exists, statusMask, jobStatus, printed, total, jobId) =
                    _healthMonitor.QueryJobStatus(printerName, documentName);

                if (exists)
                {
                    lastSpoolerJobId = jobId;
                    lastTotalPages = Math.Max(lastTotalPages, total);

                    if (printed > maxPagesPrinted)
                    {
                        maxPagesPrinted = Math.Min(printed, expectedPages);
                        await onProgress(maxPagesPrinted, expectedPages);
                    }

                    var jobHasError = (statusMask & JobErrorMask) != 0;
                    var fatalMonitorError = _healthMonitor.HasFatalHardwareError(
                        printerName,
                        out _,
                        out var fatalMessage);
                    var isDeleting = (statusMask & (0x4 | 0x100)) != 0 ||
                        jobStatus.Contains("Deleting", StringComparison.OrdinalIgnoreCase);

                    if (!jobHasError && !isDeleting)
                    {
                        observedActive = true;
                    }

                    if (jobHasError || fatalMonitorError)
                    {
                        activeErrorMessage = fatalMonitorError
                            ? fatalMessage
                            : $"Spooler error status: {jobStatus} (0x{statusMask:X})";
                        if (!inPatienceMode)
                        {
                            inPatienceMode = true;
                            await onPaused(activeErrorMessage);
                        }
                    }
                    else if (inPatienceMode)
                    {
                        inPatienceMode = false;
                        activeErrorMessage = null;
                        await onResumed();
                        deadline = DateTime.UtcNow.AddSeconds(45);
                    }
                }
                else
                {
                    if (inPatienceMode)
                    {
                        return Failed(
                            PrintFailureStage.HardwareError,
                            $"Spooler job disappeared while printer remained in error: {activeErrorMessage}",
                            expectedPages,
                            maxPagesPrinted,
                            lastSpoolerJobId);
                    }

                    if (observedActive)
                    {
                        if (lastTotalPages > 0 && lastTotalPages < expectedPages)
                        {
                            return Failed(
                                PrintFailureStage.IncompleteOutput,
                                $"Spooler reported {lastTotalPages} of {expectedPages} expected pages",
                                expectedPages,
                                maxPagesPrinted,
                                lastSpoolerJobId);
                        }

                        _logger.LogInformation(
                            "Whole-document job cleared; running post-clear hardware guard window");
                        await Task.Delay(
                            TimeSpan.FromSeconds(_settings.PostClearGuardDelaySeconds),
                            cancellationToken);

                        if (_healthMonitor.HasFatalHardwareError(
                            printerName,
                            out var code,
                            out var message))
                        {
                            return Failed(
                                PrintFailureStage.HardwareError,
                                $"Post-clear hardware error code {code}: {message}",
                                expectedPages,
                                maxPagesPrinted,
                                lastSpoolerJobId);
                        }

                        return Completed(expectedPages, lastSpoolerJobId);
                    }

                    if (lastSpoolerJobId is not null)
                    {
                        return Cancelled(
                            "Spooler job vanished without printing; likely cancelled by user",
                            expectedPages,
                            maxPagesPrinted,
                            lastSpoolerJobId);
                    }

                    if (_healthMonitor.IsHealthy(printerName, out _, out _))
                    {
                        return Completed(expectedPages, null);
                    }
                }

                await Task.Delay(2000, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            if (lastSpoolerJobId is not null || observedActive)
            {
                _healthMonitor.CancelMatchingJobs(
                    printerName,
                    documentName,
                    lastSpoolerJobId);
            }
            throw;
        }

        if (inPatienceMode)
        {
            _healthMonitor.CancelMatchingJobs(
                printerName,
                documentName,
                lastSpoolerJobId);
            return Cancelled(
                "Patience timeout exceeded",
                expectedPages,
                maxPagesPrinted,
                lastSpoolerJobId,
                PrintFailureStage.Timeout);
        }

        return Failed(
            PrintFailureStage.SpoolerVerification,
            "Job did not appear in spooler or clear successfully",
            expectedPages,
            maxPagesPrinted,
            lastSpoolerJobId);
    }

    private void ApplyPrintQuality(string printerName, string quality)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var applied = WinSpoolApi.SetPrinterQuality(printerName, quality);
            _logger.LogInformation(
                "Applied printer DEVMODE quality {quality} for {printerName} (success={applied})",
                quality,
                printerName,
                applied);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to set printer quality {quality} for {printerName}",
                quality,
                printerName);
        }
    }

    private static string FormatPageSelection(IReadOnlyList<int> pages)
    {
        if (pages.Count == 0)
        {
            throw new ArgumentException("At least one page is required", nameof(pages));
        }

        var ranges = new List<string>();
        var start = pages[0];
        var previous = pages[0];

        for (var index = 1; index < pages.Count; index++)
        {
            var current = pages[index];
            if (current == previous + 1)
            {
                previous = current;
                continue;
            }

            ranges.Add(start == previous ? start.ToString() : $"{start}-{previous}");
            start = previous = current;
        }

        ranges.Add(start == previous ? start.ToString() : $"{start}-{previous}");
        return string.Join(',', ranges);
    }

    private static DocumentPrintResult Completed(int expectedPages, string? spoolerJobId) => new()
    {
        State = PagePrintState.Completed,
        FailureStage = PrintFailureStage.None,
        SpoolerJobId = spoolerJobId,
        PagesPrinted = expectedPages,
        TotalPages = expectedPages,
        PageCountConfidence = PrintPageCountConfidence.Confirmed
    };

    private static DocumentPrintResult Failed(
        PrintFailureStage stage,
        string message,
        int expectedPages,
        int pagesPrinted = 0,
        string? spoolerJobId = null) => new()
        {
            State = PagePrintState.Failed,
            FailureStage = stage,
            ErrorMessage = message,
            SpoolerJobId = spoolerJobId,
            PagesPrinted = pagesPrinted,
            TotalPages = expectedPages,
            PageCountConfidence = pagesPrinted > 0
            ? PrintPageCountConfidence.BestEffort
            : PrintPageCountConfidence.Unknown
        };

    private static DocumentPrintResult Cancelled(
        string message,
        int expectedPages,
        int pagesPrinted,
        string? spoolerJobId,
        PrintFailureStage stage = PrintFailureStage.SpoolerVerification) => new()
        {
            State = PagePrintState.Cancelled,
            FailureStage = stage,
            ErrorMessage = message,
            SpoolerJobId = spoolerJobId,
            PagesPrinted = pagesPrinted,
            TotalPages = expectedPages,
            PageCountConfidence = pagesPrinted > 0
            ? PrintPageCountConfidence.BestEffort
            : PrintPageCountConfidence.Unknown
        };
}
