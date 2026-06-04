using PrintBit.HardwareService.Services;

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
}
