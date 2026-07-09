using Xunit;
using Moq;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Printing;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PrintBit.Tests;

public class PagePrinterTests
{
    [Fact]
    public async Task PrintPageAsync_FileDoesNotExist_ReturnsFailedState()
    {
        var healthMock = new Mock<IPrinterHealthMonitor>();
        var settings = new PrintBit.Shared.Configurations.HardwareSettings { SumatraPath = "Sumatra.exe" };
        var options = Microsoft.Extensions.Options.Options.Create(settings);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<PagePrinter>.Instance;

        var sut = new PagePrinter(logger, options, healthMock.Object);

        var result = await sut.PrintPageAsync(
            "nonexistent.pdf",
            "PrinterName",
            0,
            _ => {},
            () => {},
            CancellationToken.None);

        Assert.Equal(PagePrintState.Failed, result.State);
        Assert.Equal(PrintFailureStage.Validation, result.FailureStage);
    }
}
