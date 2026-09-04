using System.Text.Json.Serialization;

namespace PrintBit.Infrastructure.IPC;

public abstract record WorkerHardwareCommand
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;
}

public sealed record DispenseCoinsCommand : WorkerHardwareCommand
{
    [JsonPropertyName("coinCount")]
    public int CoinCount { get; init; }

    [JsonPropertyName("timeoutMs")]
    public int? TimeoutMs { get; init; }
}

public sealed record LockCoinSlotCommand : WorkerHardwareCommand
{
    [JsonPropertyName("ownerId")]
    public string OwnerId { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record UnlockCoinSlotCommand : WorkerHardwareCommand
{
    [JsonPropertyName("ownerId")]
    public string OwnerId { get; init; } = string.Empty;
}

public sealed record AnnounceKioskIpCommand : WorkerHardwareCommand
{
    [JsonPropertyName("ip")]
    public string Ip { get; init; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;
}

public sealed record DispenseCoinsResponse
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = "DispenseCoins";

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("dispensedCoins")]
    public int DispensedCoins { get; init; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

public sealed record LockCoinSlotResponse
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = "LockCoinSlot";

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
}

public sealed record UnlockCoinSlotResponse
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = "UnlockCoinSlot";

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("unlocked")]
    public bool Unlocked { get; init; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
}

public sealed record AnnounceKioskIpResponse
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = "AnnounceKioskIp";

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
}

public sealed record HardwareErrorResponse
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    [JsonPropertyName("success")]
    public bool Success { get; init; } = false;

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
}

public sealed record HardwareCommandResponse
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("dispensedCoins")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DispensedCoins { get; init; }

    [JsonPropertyName("unlocked")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Unlocked { get; init; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
}
