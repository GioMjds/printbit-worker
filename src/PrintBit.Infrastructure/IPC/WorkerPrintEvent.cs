namespace PrintBit.Infrastructure.IPC;

public sealed record WorkerPrintEvent
{
    public WorkerPrintEventType Type { get; init; }

    public string? TransactionId { get; init; }

    public string? SpoolerCorrelationKey { get; init; }

    public string? SpoolerJobId { get; init; }

    public string? FileName { get; init; }

    public string? PrinterName { get; init; }

    public string? FailureStage { get; init; }

    public string? Message { get; init; }

    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
}
