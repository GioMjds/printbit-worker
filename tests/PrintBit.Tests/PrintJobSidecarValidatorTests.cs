using PrintBit.HardwareService.Services;

namespace PrintBit.Tests;

public class PrintJobSidecarValidatorTests
{
    [Fact]
    public void TryParse_AcceptsLegacySettingsOnlySidecar()
    {
        const string json = "{\"copies\":2,\"color\":true,\"pageRange\":\"1-3\",\"orientation\":\"landscape\"}";

        var valid = PrintJobSidecarValidator.TryParse(
            json,
            "tx-1_spool-1_1700000000000.json",
            out var settings,
            out var error);

        Assert.True(valid);
        Assert.Null(error);
        Assert.NotNull(settings);
        Assert.Equal(2, settings!.Copies);
        Assert.True(settings.Color);
        Assert.Equal("1-3", settings.PageRange);
        Assert.Equal("landscape", settings.Orientation);
    }

    [Fact]
    public void TryParse_AcceptsV2SidecarWhoseIdsMatchFilename()
    {
        const string json = "{\"schemaVersion\":2,\"transactionId\":\"tx-1\",\"spoolerCorrelationKey\":\"spool-1\",\"copies\":2}";

        var valid = PrintJobSidecarValidator.TryParse(
            json,
            "tx-1_spool-1_1700000000000.pdf",
            out var settings,
            out var error);

        Assert.True(valid);
        Assert.Null(error);
        Assert.NotNull(settings);
        Assert.Equal(2, settings!.Copies);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"transactionId\":\"tx-1\",\"spoolerCorrelationKey\":\"spool-1\"}")]
    [InlineData("{\"schemaVersion\":2,\"transactionId\":\"tx-1\"}")]
    [InlineData("{\"transactionId\":\"tx-1\",\"spoolerCorrelationKey\":\"spool-1\"}")]
    public void TryParse_RejectsUnsupportedOrPartialV2Envelope(string json)
    {
        var valid = PrintJobSidecarValidator.TryParse(
            json,
            "tx-1_spool-1_1700000000000.json",
            out var settings,
            out var error);

        Assert.False(valid);
        Assert.Null(settings);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_RejectsV2IdsThatDoNotMatchFilename()
    {
        const string json = "{\"schemaVersion\":2,\"transactionId\":\"tx-1\",\"spoolerCorrelationKey\":\"other-spool\"}";

        var valid = PrintJobSidecarValidator.TryParse(
            json,
            "tx-1_spool-1_1700000000000.json",
            out var settings,
            out var error);

        Assert.False(valid);
        Assert.Null(settings);
        Assert.NotNull(error);
    }
}
