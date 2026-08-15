using System;
using System.Threading;
using System.Threading.Tasks;

namespace PrintBit.Infrastructure.Services.PrintService;

public interface IPrinterHealthMonitor
{
    bool IsHealthy(string printerName, out int winSpoolStatus, out string winSpoolDesc);
    bool HasFatalHardwareError(string printerName, out int errorCode, out string errorMessage);
    Task<bool> WaitForPrinterHealthyAsync(
        string printerName,
        int timeoutSeconds,
        CancellationToken cancellationToken);
    Task RecoverAsync(CancellationToken cancellationToken);
    (bool JobExists, uint StatusMask, string JobStatus, int PagesPrinted, int TotalPages, string? JobId) QueryJobStatus(
        string printerName,
        string documentName);
    void CancelMatchingJobs(string printerName, string documentName, string? spoolerJobId);
    /// <summary>
    /// Best-effort pause of the live spooler job(s) matching the document name
    /// (or explicit jobId) via WinSpool SetJob(JOB_CONTROL_PAUSE). Used for a
    /// user-initiated pause so a page already handed to the spooler halts
    /// mid-way rather than only the next page being held. Never throws.
    /// </summary>
    void PauseMatchingJobs(string printerName, string documentName, string? spoolerJobId);
    /// <summary>
    /// Best-effort resume of paused spooler job(s) matching the document name
    /// (or explicit jobId) via WinSpool SetJob(JOB_CONTROL_RESUME). Never throws.
    /// </summary>
    void ResumeMatchingJobs(string printerName, string documentName, string? spoolerJobId);
    /// <summary>
    /// Dismiss the EPSON Status Monitor popup (if visible), reset the printer
    /// error status via WinSpool, and nudge the printer back online.
    /// Call this when the operator clicks Resume so that the next
    /// <see cref="IsHealthy"/> check sees a clean printer state.
    /// </summary>
    Task DismissAndResetAsync(string printerName, CancellationToken cancellationToken);
}
