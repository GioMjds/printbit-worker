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
        Action<string> onPaused,
        Action onResumed,
        CancellationToken cancellationToken);
}
