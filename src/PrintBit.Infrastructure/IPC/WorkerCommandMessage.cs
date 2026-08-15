using System.Text.Json.Serialization;

namespace PrintBit.Infrastructure.IPC;

public class WorkerCommandMessage
{
    [JsonPropertyName("protocolVersion")]
    public int? ProtocolVersion { get; set; }

    [JsonPropertyName("commandId")]
    public string? CommandId { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("transactionId")]
    public string? TransactionId { get; set; }

    [JsonPropertyName("spoolerCorrelationKey")]
    public string SpoolerCorrelationKey { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("timestampUtc")]
    public string? TimestampUtc { get; set; }
}
