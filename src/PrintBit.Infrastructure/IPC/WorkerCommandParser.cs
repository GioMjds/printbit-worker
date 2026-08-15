using System.Text;
using System.Text.Json;

namespace PrintBit.Infrastructure.IPC;

public static class WorkerCommandParser
{
    public const int DefaultMaxMessageBytes = 8192;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static WorkerCommandMessage? ParseLine(string line, int maxBytes = DefaultMaxMessageBytes)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        if (Encoding.UTF8.GetByteCount(line) > maxBytes)
            return null;

        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var cmd = JsonSerializer.Deserialize<WorkerCommandMessage>(line, Options);
            if (cmd == null || string.IsNullOrWhiteSpace(cmd.Type) || string.IsNullOrWhiteSpace(cmd.SpoolerCorrelationKey))
            {
                return null;
            }

            var hasV2EnvelopeField = HasProperty(document.RootElement, "protocolVersion")
                || HasProperty(document.RootElement, "commandId");
            if (hasV2EnvelopeField && (cmd.ProtocolVersion != 2 || string.IsNullOrWhiteSpace(cmd.CommandId)))
            {
                return null;
            }

            return cmd;
        }
        catch (JsonException)
        {
            return null;
        }
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
