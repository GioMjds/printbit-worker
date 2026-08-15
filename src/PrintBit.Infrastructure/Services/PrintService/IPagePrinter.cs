using System;
using System.Threading;
using System.Threading.Tasks;
using PrintBit.Shared.Printing;

namespace PrintBit.Infrastructure.Services.PrintService;

public interface IPagePrinter
{
    Task<PagePrintResult> PrintPageAsync(
        string filePath,
        string printerName,
        int sequenceIndex,
        Func<string, Task> onPaused,
        Func<Task> onResumed,
        Func<Task> resumeSignalProvider,
        CancellationToken cancellationToken);
}
