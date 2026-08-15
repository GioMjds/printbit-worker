using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Printing;

namespace PrintBit.HardwareService.Services;

public sealed class PrintJobSidecar
{
    [JsonPropertyName("copies")]
    public int Copies { get; init; } = 1;

    [JsonPropertyName("color")]
    public bool Color { get; init; }

    [JsonPropertyName("pageRange")]
    public string? PageRange { get; init; }

    [JsonPropertyName("orientation")]
    public string? Orientation { get; init; }

    [JsonPropertyName("schemaVersion")]
    public int? SchemaVersion { get; init; }

    [JsonPropertyName("transactionId")]
    public string? TransactionId { get; init; }

    [JsonPropertyName("spoolerCorrelationKey")]
    public string? SpoolerCorrelationKey { get; init; }

    public PrintJobSettings ToPrintJobSettings() => new()
    {
        Copies = Copies,
        Color = Color,
        PageRange = PageRange,
        Orientation = Orientation
    };
}

public static class PrintJobSidecarValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool TryParse(
        string json,
        string fileName,
        out PrintJobSettings? settings,
        out string? error)
    {
        settings = null;
        error = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Sidecar must be a JSON object.";
                return false;
            }

            var sidecar = JsonSerializer.Deserialize<PrintJobSidecar>(json, JsonOptions);
            if (sidecar is null)
            {
                error = "Sidecar could not be parsed.";
                return false;
            }

            var hasV2EnvelopeField = HasProperty(document.RootElement, "schemaVersion")
                || HasProperty(document.RootElement, "transactionId")
                || HasProperty(document.RootElement, "spoolerCorrelationKey");

            if (hasV2EnvelopeField && !HasValidV2Envelope(sidecar, fileName))
            {
                error = "Sidecar v2 identity envelope is incomplete, unsupported, or does not match its filename.";
                return false;
            }

            settings = sidecar.ToPrintJobSettings();
            return true;
        }
        catch (JsonException)
        {
            error = "Sidecar contains invalid JSON.";
            return false;
        }
    }

    private static bool HasValidV2Envelope(PrintJobSidecar sidecar, string fileName)
    {
        if (sidecar.SchemaVersion != 2
            || string.IsNullOrWhiteSpace(sidecar.TransactionId)
            || string.IsNullOrWhiteSpace(sidecar.SpoolerCorrelationKey))
        {
            return false;
        }

        var (transactionId, spoolerCorrelationKey) = PrintJobFileName.TryParseCorrelation(fileName);
        return string.Equals(sidecar.TransactionId, transactionId, StringComparison.Ordinal)
            && string.Equals(sidecar.SpoolerCorrelationKey, spoolerCorrelationKey, StringComparison.Ordinal);
    }

    private static bool HasProperty(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
