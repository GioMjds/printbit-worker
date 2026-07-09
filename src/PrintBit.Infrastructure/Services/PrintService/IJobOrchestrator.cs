using System.Threading;
using System.Threading.Tasks;
using PrintBit.Infrastructure.IPC;

namespace PrintBit.Infrastructure.Services.PrintService;

public interface IJobOrchestrator
{
    Task ProcessJobAsync(
        string pdfPath,
        string transactionId,
        string spoolerCorrelationKey,
        int copies,
        CancellationToken cancellationToken);
}
