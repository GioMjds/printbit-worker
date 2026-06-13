namespace PrintBit.Infrastructure.Services.PrintService;

public interface IPrintHealthCoordinator
{
    IDisposable BeginAttempt(string printerName, string expectedDocument);

    void ReportFatalHardwareError(
        string printerName,
        int errorCode,
        string message);

    bool TryGetFatalHardwareError(
        string printerName,
        out HardwareErrorSignal signal);
}
