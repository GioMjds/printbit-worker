using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;

namespace PrintBit.Tests;

public class PrinterProfileResolverTests
{
    [Fact]
    public void Resolve_Standard_UsesConfiguredStandardQueue()
    {
        var settings = new HardwareSettings
        {
            PrinterName = "EPSON L5290 Series",
            PrinterProfiles = new PrinterProfileSettings
            {
                Standard = "PrintBit - Standard",
                High = "PrintBit - High"
            }
        };

        Assert.Equal(
            "PrintBit - Standard",
            PrinterProfileResolver.Resolve(settings, "standard"));
    }

    [Fact]
    public void Resolve_Standard_FallsBackToPhysicalPrinterName()
    {
        var settings = new HardwareSettings
        {
            PrinterName = "EPSON L5290 Series"
        };

        Assert.Equal(
            "EPSON L5290 Series",
            PrinterProfileResolver.Resolve(settings, "standard"));
    }

    [Fact]
    public void Resolve_High_UsesConfiguredHighQueue()
    {
        var settings = new HardwareSettings
        {
            PrinterName = "EPSON L5290 Series",
            PrinterProfiles = new PrinterProfileSettings
            {
                High = "PrintBit - High"
            }
        };

        Assert.Equal(
            "PrintBit - High",
            PrinterProfileResolver.Resolve(settings, "HIGH"));
    }

    [Fact]
    public void Resolve_HighWithoutConfiguredQueue_RejectsDispatch()
    {
        var settings = new HardwareSettings
        {
            PrinterName = "EPSON L5290 Series"
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => PrinterProfileResolver.Resolve(settings, "high"));

        Assert.Contains("High-quality printer profile", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("draft")]
    [InlineData("600dpi")]
    public void Resolve_UnknownQuality_RejectsDispatch(string? quality)
    {
        var settings = new HardwareSettings
        {
            PrinterName = "EPSON L5290 Series",
            PrinterProfiles = new PrinterProfileSettings
            {
                High = "PrintBit - High"
            }
        };

        Assert.Throws<ArgumentException>(
            () => PrinterProfileResolver.Resolve(settings, quality));
    }
}
