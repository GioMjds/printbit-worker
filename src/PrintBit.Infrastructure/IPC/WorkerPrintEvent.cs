using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PrintBit.Infrastructure.IPC;

public sealed record WorkerPrintEvent
{
    [JsonPropertyName("type")]
    public WorkerPrintEventType Type { get; init; }

    [JsonPropertyName("transactionId")]
    public string? TransactionId { get; init; }

    [JsonPropertyName("spoolerCorrelationKey")]
    public string? SpoolerCorrelationKey { get; init; }

    [JsonPropertyName("spoolerJobId")]
    public string? SpoolerJobId { get; init; }

    [JsonPropertyName("fileName")]
    public string? FileName { get; init; }

    [JsonPropertyName("printerName")]
    public string? PrinterName { get; init; }

    [JsonPropertyName("failureStage")]
    public string? FailureStage { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("timestampUtc")]
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("pagesPrinted")]
    public int? PagesPrinted { get; init; }

    [JsonPropertyName("totalPages")]
    public int? TotalPages { get; init; }

    [JsonPropertyName("pageCountConfidence")]
    public string? PageCountConfidence { get; init; }

    [JsonPropertyName("completedCount")]
    public int? CompletedCount { get; init; }

    [JsonPropertyName("outcome")]
    public string? Outcome { get; init; }

    [JsonPropertyName("totalCopies")]
    public int? TotalCopies { get; init; }

    [JsonPropertyName("totalExpected")]
    public int? TotalExpected { get; init; }

    [JsonPropertyName("cancelledCount")]
    public int? CancelledCount { get; init; }

    [JsonPropertyName("failedCount")]
    public int? FailedCount { get; init; }

    [JsonPropertyName("pages")]
    public List<WorkerPrintPageResult>? Pages { get; init; }

    [JsonPropertyName("startedAt")]
    public DateTime? StartedAt { get; init; }

    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; init; }

}
