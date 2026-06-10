using System.Text.Json;
using System.Text.Json.Serialization;
using PrintBit.HardwareService.Services;
using PrintBit.Infrastructure.IPC;

namespace PrintBit.Tests;

public class WorkerPrintEventTests
{
    [Fact]
    public void TryParseCorrelation_ParsesExpectedFormat()
    {
        var fileName = "tx-1_spool-1_1700000000000.pdf";

        var parsed = PrintQueueWatcherService.TryParseCorrelation(fileName);

        Assert.Equal("tx-1", parsed.TransactionId);
        Assert.Equal("spool-1", parsed.SpoolerCorrelationKey);
    }

    [Fact]
    public void TryParseCorrelation_WithMissingParts_ReturnsNulls()
    {
        var parsed = PrintQueueWatcherService.TryParseCorrelation("justfile.pdf");

        Assert.Null(parsed.TransactionId);
        Assert.Null(parsed.SpoolerCorrelationKey);
    }

    [Fact]
    public void PrinterErrorPayload_UsesExpectedContract()
    {
        // Serialize the real WorkerPrintEvent (not an anonymous object) so
        // the test catches any future regression in casing or in the
        // JsonStringEnumConverter that WorkerEventPipeClient uses.
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };

        var evt = new WorkerPrintEvent
        {
            Type = WorkerPrintEventType.PrinterError,
            PrinterName = "EPSON L5290 Series",
            FailureStage = "hardware_error",
            Message = "Printer hardware error detected (code 2). Check paper, ink, or connection.",
        };

        var json = JsonSerializer.Serialize(evt, jsonOptions);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("PrinterError", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("EPSON L5290 Series", doc.RootElement.GetProperty("printerName").GetString());
        Assert.Equal("hardware_error", doc.RootElement.GetProperty("failureStage").GetString());
        Assert.StartsWith("Printer hardware error detected (code 2).", doc.RootElement.GetProperty("message").GetString());
        // TimestampUtc defaults to DateTime.UtcNow, so just assert it is an ISO-8601 string.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T", doc.RootElement.GetProperty("timestampUtc").GetString());
    }
}
