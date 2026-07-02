namespace PrintBit.Infrastructure.IPC;

public enum WorkerPrintEventType
{
    PrintStarted = 0,
    PrintSucceeded = 1,
    PrintFailed = 2,
    PrinterOffline = 3,
    PrinterOnline = 4,
    PrinterError = 5,
    PrintProgress = 6,
}
