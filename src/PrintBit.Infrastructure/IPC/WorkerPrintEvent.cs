using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PrintBit.Infrastructure.IPC;

public sealed class WorkerPrintPageResult
{
    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("copy")]
    public int Copy { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty; // "completed", "cancelled", "failed"
}

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
    public int? PagesPrinted { get; init; }
    public int? TotalPages { get; init; }

    public int? PageNumber { get; init; }
    public int? CopyNumber { get; init; }
    public int? FailedPageNumber { get; init; }
    public int? FailedCopyNumber { get; init; }
    public int? ResumingPageNumber { get; init; }
    public int? ResumingCopyNumber { get; init; }
    public int? CompletedCount { get; init; }
    public int? TotalCount { get; init; }
    public string? Outcome { get; init; }
    public int? TotalCopies { get; init; }
    public int? TotalExpected { get; init; }
    public int? CancelledCount { get; init; }
    public int? FailedCount { get; init; }
    public List<WorkerPrintPageResult>? Pages { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? ErrorMessage { get; init; }
}
