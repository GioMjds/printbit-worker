namespace PrintBit.Infrastructure.IPC;

public enum WorkerPrintEventType
{
    PrintStarted = 0,
    PrintSucceeded = 1,
    PrintFailed = 2,
    PrinterOffline = 3,
    PrinterOnline = 4,
    PrinterError = 5,
    PowerStatusChanged = 6,
    PowerStatusSnapshot = 7,
    PrinterStatusSnapshot = 8
}

