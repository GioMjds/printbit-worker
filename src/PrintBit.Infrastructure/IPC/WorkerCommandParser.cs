using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PrintBit.Infrastructure.Services.PrintService;

namespace PrintBit.Infrastructure.IPC;

public static class WorkerCommandParser
{
    public const int DefaultMaxMessageBytes = 8192;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    /// <summary>
    /// Reads a single line from the given stream up to a maximum byte limit, stopping at '\n'.
    /// Returns the line (without '\r' or '\n') and whether the line exceeded the byte limit.
    /// </summary>
    public static async Task<(string? Line, bool Oversized)> ReadLineWithLimitAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        var limit = maxBytes > 0 ? maxBytes : DefaultMaxMessageBytes;
        using var ms = new MemoryStream();
        var buffer = new byte[1024];
        var totalBytes = 0;
        var oversized = false;
        var foundNewline = false;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            if (read == 0)
            {
                break;
            }

            var newlineIndex = -1;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    newlineIndex = i;
                    break;
                }
            }

            if (newlineIndex >= 0)
            {
                foundNewline = true;
                totalBytes += newlineIndex;
                if (totalBytes > limit)
                {
                    oversized = true;
                }
                else
                {
                    ms.Write(buffer, 0, newlineIndex);
                }
                break;
            }
            else
            {
                totalBytes += read;
                if (totalBytes > limit)
                {
                    oversized = true;
                    break;
                }
                ms.Write(buffer, 0, read);
            }
        }

        if (oversized)
        {
            return (null, true);
        }

        if (ms.Length == 0 && totalBytes == 0 && !foundNewline)
        {
            return (null, false);
        }

        var line = Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\r');
        return (line, false);
    }

    /// <summary>
    /// Attempts to strictly parse a JSON line into a PrinterRecoveryCommand.
    /// Validates payload byte count, JSON formatting, RequestId presence, and command Type.
    /// Preserves any extracted RequestId even when validation fails.
    /// </summary>
    public static bool TryParse(
        string? line,
        int maxBytes,
        [NotNullWhen(true)] out PrinterRecoveryCommand? command,
        [NotNullWhen(false)] out string? errorDetail,
        out string requestId)
    {
        command = null;
        errorDetail = null;
        requestId = string.Empty;

        if (string.IsNullOrWhiteSpace(line))
        {
            errorDetail = "Payload is empty";
            return false;
        }

        var limit = maxBytes > 0 ? maxBytes : DefaultMaxMessageBytes;
        if (Encoding.UTF8.GetByteCount(line) > limit)
        {
            errorDetail = $"Payload exceeds maximum allowed size of {limit} bytes";
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            errorDetail = $"Malformed JSON: {ex.Message}";
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                errorDetail = "Payload must be a JSON object";
                return false;
            }

            if (TryGetPropertyCaseInsensitive(doc.RootElement, "requestId", out var reqElem) &&
                reqElem.ValueKind == JsonValueKind.String)
            {
                requestId = reqElem.GetString() ?? string.Empty;
            }

            if (!TryGetPropertyCaseInsensitive(doc.RootElement, "type", out var typeElem) ||
                typeElem.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(typeElem.GetString()))
            {
                errorDetail = "Command type is required";
                command = null;
                return false;
            }

            try
            {
                command = JsonSerializer.Deserialize<PrinterRecoveryCommand>(line, JsonOptions);
            }
            catch (Exception ex)
            {
                errorDetail = $"Invalid command payload: {ex.Message}";
                command = null;
                return false;
            }

            if (command is null)
            {
                errorDetail = "Command could not be deserialized";
                return false;
            }

            if (string.IsNullOrWhiteSpace(command.RequestId))
            {
                errorDetail = "RequestId is required";
                command = null;
                return false;
            }

            if (!Enum.IsDefined(typeof(PrinterRecoveryCommandType), command.Type))
            {
                errorDetail = $"Unknown command type: {command.Type}";
                command = null;
                return false;
            }

            requestId = command.RequestId;
            return true;
        }
    }

    public static bool TryParse(
        string? line,
        int maxBytes,
        [NotNullWhen(true)] out PrinterRecoveryCommand? command,
        [NotNullWhen(false)] out string? errorDetail)
    {
        return TryParse(line, maxBytes, out command, out errorDetail, out _);
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
