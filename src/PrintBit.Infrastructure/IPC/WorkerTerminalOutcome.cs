namespace PrintBit.Infrastructure.IPC;

public static class WorkerTerminalOutcome
{
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string PartiallyCompleted = "partially_completed";
    public const string Unknown = "unknown";
}
