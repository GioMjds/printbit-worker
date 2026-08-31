using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Infrastructure.IPC;
using PrintBit.Shared.Configurations;
using PrintBit.Shared.Printing;

namespace PrintBit.Infrastructure.Services.PrintService;

public sealed class JobOrchestrator : IJobOrchestrator
{
    private readonly ILogger<JobOrchestrator> _logger;
    private readonly HardwareSettings _settings;
    private readonly IDocumentPrinter _documentPrinter;
    private readonly IPrinterHealthMonitor _healthMonitor;
    private readonly IWorkerEventPipeClient _eventPipe;

    public JobOrchestrator(
        ILogger<JobOrchestrator> logger,
        IOptions<HardwareSettings> options,
        IDocumentPrinter documentPrinter,
        IPrinterHealthMonitor healthMonitor,
        IWorkerEventPipeClient eventPipe)
    {
        _logger = logger;
        _settings = options.Value;
        _documentPrinter = documentPrinter;
        _healthMonitor = healthMonitor;
        _eventPipe = eventPipe;
    }

    public async Task<PrintJobResult> ProcessJobAsync(
        PrintJobRequest request,
        string jsonFilePath,
        CancellationToken cancellationToken)
    {
        _ = jsonFilePath;
        var fileName = Path.GetFileName(request.FilePath);
        var (transactionId, spoolerCorrelationKey) =
            PrintJobFileName.TryParseCorrelation(fileName);

        if (transactionId is null || spoolerCorrelationKey is null)
        {
            return PrintJobResult.Failed(
                PrintFailureStage.Validation,
                "Filename does not match the tx_spool layout");
        }

        var pdfPageCount = PdfPageCounter.Count(request.FilePath, _settings.QpdfPath);
        if (pdfPageCount is null or <= 0)
        {
            _logger.LogError(
                "Could not determine PDF page count for {file}",
                request.FilePath);
            return PrintJobResult.Failed(
                PrintFailureStage.Validation,
                "Could not determine PDF page count or PDF is corrupt");
        }

        var pagesToPrint = GetPagesInRange(
            pdfPageCount.Value,
            request.Settings.PageRange);
        if (pagesToPrint.Count == 0)
        {
            return PrintJobResult.Failed(
                PrintFailureStage.Validation,
                "Page range did not select any pages");
        }

        var totalCopies = Math.Max(1, request.Settings.Copies);
        var manifest = BuildManifest(pagesToPrint, totalCopies);
        var startedAt = DateTime.UtcNow;

        await SendEventAsync(new WorkerPrintEvent
        {
            Type = WorkerPrintEventType.PrintStarted,
            TransactionId = transactionId,
            SpoolerCorrelationKey = spoolerCorrelationKey,
            PrinterName = request.PrinterName,
            FileName = fileName,
            TotalPages = pagesToPrint.Count,
            TotalExpected = manifest.Count,
            TotalCopies = totalCopies,
            TimestampUtc = startedAt
        }, cancellationToken);

        PrintFailureStage failureStage = PrintFailureStage.None;
        string? failureMessage = null;
        string? spoolerJobId = null;
        var failureConfidence = PrintPageCountConfidence.Unknown;

        for (var copyNumber = 1; copyNumber <= totalCopies; copyNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copyEntries = manifest
                .Where(entry => entry.CopyNumber == copyNumber)
                .OrderBy(entry => entry.SequenceIndex)
                .ToList();

            if (!_healthMonitor.IsHealthy(request.PrinterName, out _, out _) &&
                !await WaitForPreFlightHealthAsync(
                    request,
                    copyEntries[0],
                    cancellationToken))
            {
                _healthMonitor.HasFatalHardwareError(request.PrinterName, out _, out var fatalDesc);
                copyEntries[0].State = PagePrintState.Failed;
                copyEntries[0].ErrorMessage = string.IsNullOrWhiteSpace(fatalDesc)
                    ? "Pause timeout exceeded during pre-flight health wait"
                    : $"Printer remained unhealthy during pre-flight health wait: {fatalDesc}";
                CancelRemaining(manifest, copyEntries[0].SequenceIndex + 1);
                failureStage = PrintFailureStage.HardwareError;
                failureMessage = copyEntries[0].ErrorMessage;
                break;
            }

            var dispatchStartedAt = DateTime.UtcNow;
            foreach (var entry in copyEntries)
            {
                entry.State = PagePrintState.Printing;
                entry.StartedAt = dispatchStartedAt;
            }

            var result = await _documentPrinter.PrintDocumentAsync(
                request.FilePath,
                request.PrinterName,
                copyNumber,
                pagesToPrint,
                request.Settings,
                (printed, _) =>
                {
                    MarkProgress(copyEntries, printed);
                    return Task.CompletedTask;
                },
                error =>
                {
                    var activeEntry = GetActiveEntry(copyEntries);
                    _logger.LogWarning(
                        "Whole-document copy {copyNumber} paused at page {pageNumber}: {error}",
                        activeEntry.CopyNumber,
                        activeEntry.PageNumber,
                        error);
                    return Task.CompletedTask;
                },
                () =>
                {
                    var activeEntry = GetActiveEntry(copyEntries);
                    _logger.LogInformation(
                        "Whole-document copy {copyNumber} resumed at page {pageNumber}",
                        activeEntry.CopyNumber,
                        activeEntry.PageNumber);
                    return Task.CompletedTask;
                },
                cancellationToken);

            spoolerJobId = result.SpoolerJobId ?? spoolerJobId;
            MarkProgress(copyEntries, result.PagesPrinted);

            if (result.State == PagePrintState.Completed)
            {
                MarkProgress(copyEntries, copyEntries.Count);
                continue;
            }

            var failedEntry = copyEntries.FirstOrDefault(
                entry => entry.State != PagePrintState.Completed);
            if (failedEntry is not null)
            {
                failedEntry.State = PagePrintState.Failed;
                failedEntry.ErrorMessage = result.ErrorMessage;
                failedEntry.CompletedAt = DateTime.UtcNow;
                CancelRemaining(manifest, failedEntry.SequenceIndex + 1);
            }

            failureStage = result.FailureStage == PrintFailureStage.None
                ? PrintFailureStage.SpoolerVerification
                : result.FailureStage;
            failureMessage = result.ErrorMessage ?? "Whole-document print failed";
            failureConfidence = result.PageCountConfidence;
            break;
        }

        var completedAt = DateTime.UtcNow;
        var completedCount = manifest.Count(entry => entry.State == PagePrintState.Completed);
        var failedCount = manifest.Count(entry => entry.State == PagePrintState.Failed);
        var cancelledCount = manifest.Count(entry => entry.State == PagePrintState.Cancelled);
        var fullyCompleted = completedCount == manifest.Count && failedCount == 0;
        var outcome = fullyCompleted
            ? "completed"
            : completedCount > 0
                ? "partially_completed"
                : "failed";
        var pageCountConfidence = fullyCompleted
            ? PrintPageCountConfidence.Confirmed
            : failureConfidence;

        var terminalEvent = new WorkerPrintEvent
        {
            Type = fullyCompleted
                ? WorkerPrintEventType.PrintSucceeded
                : WorkerPrintEventType.PrintFailed,
            TransactionId = transactionId,
            SpoolerCorrelationKey = spoolerCorrelationKey,
            SpoolerJobId = spoolerJobId,
            PrinterName = request.PrinterName,
            FileName = fileName,
            Outcome = outcome,
            TotalPages = manifest.Count,
            PagesPrinted = completedCount,
            PageCountConfidence = pageCountConfidence,
            TotalCopies = totalCopies,
            TotalExpected = manifest.Count,
            CompletedCount = completedCount,
            CancelledCount = cancelledCount,
            FailedCount = failedCount,
            Pages = manifest.Select(entry => new WorkerPrintPageResult
            {
                Page = entry.PageNumber,
                Copy = entry.CopyNumber,
                State = entry.State.ToString().ToLowerInvariant()
            }).ToList(),
            StartedAt = startedAt,
            CompletedAt = completedAt,
            FailureStage = fullyCompleted ? null : failureStage.ToString(),
            Message = fullyCompleted
                ? "Print job completed successfully"
                : $"Print job finished with state: {outcome}. {failureMessage}",
            TimestampUtc = completedAt
        };
        await SendEventAsync(terminalEvent, cancellationToken);

        if (!fullyCompleted)
        {
            return PrintJobResult.Failed(
                failureStage,
                failureMessage ?? "Print job failed",
                spoolerJobId: spoolerJobId,
                pagesPrinted: completedCount,
                totalPages: manifest.Count,
                pageCountConfidence: pageCountConfidence);
        }

        return new PrintJobResult
        {
            Success = true,
            Message = "Print job completed",
            SumatraProcessSucceeded = true,
            VerificationSucceeded = true,
            FailureStage = PrintFailureStage.None,
            SpoolerJobId = spoolerJobId,
            SpoolerPrinterName = request.PrinterName,
            PagesPrinted = completedCount,
            TotalPages = manifest.Count,
            PageCountConfidence = PrintPageCountConfidence.Confirmed
        };
    }

    private static void MarkProgress(
        IReadOnlyList<PagePrintEntry> copyEntries,
        int printedWithinCopy)
    {
        var completedWithinCopy = Math.Clamp(
            printedWithinCopy,
            0,
            copyEntries.Count);

        for (var index = 0; index < completedWithinCopy; index++)
        {
            var entry = copyEntries[index];
            if (entry.State == PagePrintState.Completed) continue;

            entry.State = PagePrintState.Completed;
            entry.CompletedAt = DateTime.UtcNow;
        }
    }

    private async Task<bool> WaitForPreFlightHealthAsync(
        PrintJobRequest request,
        PagePrintEntry entry,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Whole-document copy {copyNumber} waiting for printer health before page {pageNumber}",
            entry.CopyNumber,
            entry.PageNumber);

        var deadline = DateTime.UtcNow.AddMinutes(_settings.PauseTimeoutMinutes);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(2000, cancellationToken);

            if (_healthMonitor.IsHealthy(request.PrinterName, out _, out _))
            {
                _logger.LogInformation(
                    "Printer recovered before whole-document copy {copyNumber}",
                    entry.CopyNumber);
                return true;
            }
        }

        return false;
    }

    private Task SendEventAsync(
        WorkerPrintEvent evt,
        CancellationToken cancellationToken) =>
        _eventPipe.SendAsync(evt, cancellationToken);

    private static PagePrintEntry GetActiveEntry(
        IReadOnlyList<PagePrintEntry> entries) =>
        entries.FirstOrDefault(entry => entry.State != PagePrintState.Completed) ??
        entries[^1];

    private static List<PagePrintEntry> BuildManifest(
        IReadOnlyList<int> pages,
        int totalCopies)
    {
        var manifest = new List<PagePrintEntry>(pages.Count * totalCopies);
        var sequenceIndex = 0;
        for (var copyNumber = 1; copyNumber <= totalCopies; copyNumber++)
        {
            foreach (var pageNumber in pages)
            {
                manifest.Add(new PagePrintEntry
                {
                    PageNumber = pageNumber,
                    CopyNumber = copyNumber,
                    SequenceIndex = sequenceIndex++,
                    State = PagePrintState.Pending
                });
            }
        }
        return manifest;
    }

    private static List<int> GetPagesInRange(int pdfPageCount, string? pageRange)
    {
        if (string.IsNullOrWhiteSpace(pageRange))
        {
            return Enumerable.Range(1, pdfPageCount).ToList();
        }

        var pages = new List<int>();
        var parts = pageRange.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            if (part.Contains('-'))
            {
                var endpoints = part.Split(
                    '-',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries);
                if (endpoints.Length != 2 ||
                    !int.TryParse(endpoints[0], out var start) ||
                    !int.TryParse(endpoints[1], out var end))
                {
                    continue;
                }

                var step = start <= end ? 1 : -1;
                for (var page = start;
                     start <= end ? page <= end : page >= end;
                     page += step)
                {
                    if (page >= 1 && page <= pdfPageCount)
                    {
                        pages.Add(page);
                    }
                }
            }
            else if (int.TryParse(part, out var page) &&
                     page >= 1 && page <= pdfPageCount)
            {
                pages.Add(page);
            }
        }

        return pages;
    }

    private static void CancelRemaining(
        IReadOnlyList<PagePrintEntry> manifest,
        int startIndex)
    {
        for (var index = startIndex; index < manifest.Count; index++)
        {
            if (manifest[index].State != PagePrintState.Completed)
            {
                manifest[index].State = PagePrintState.Cancelled;
            }
        }
    }
}
