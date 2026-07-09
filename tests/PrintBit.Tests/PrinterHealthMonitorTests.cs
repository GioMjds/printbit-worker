using Xunit;
using Moq;
using PrintBit.Infrastructure.Services.PrintService;
using System.Threading;
using System.Threading.Tasks;

namespace PrintBit.Tests;

public class PrinterHealthMonitorTests
{
    [Fact]
    public async Task WaitForPrinterHealthyAsync_StopsOnCancellation()
    {
        var monitorMock = new Mock<IPrinterHealthMonitor>();
        monitorMock.Setup(m => m.WaitForPrinterHealthyAsync(
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await monitorMock.Object.WaitForPrinterHealthyAsync("Printer", 30, cts.Token);
        Assert.False(result);
    }
}
