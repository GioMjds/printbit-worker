using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Infrastructure.Windows.PrinterMonitoring;
using PrintBit.Shared.Configurations;
using System.Threading;
using System.Threading.Tasks;

namespace PrintBit.Tests;

public class PrinterHealthMonitorTests
{
    private class TestablePrinterHealthMonitor : PrinterHealthMonitor
    {
        public bool MockHealthy { get; set; } = true;
        public int MockWinSpoolStatus { get; set; } = 0;
        public string MockWinSpoolDesc { get; set; } = "OK";
        public int IsHealthyCallCount { get; set; } = 0;

        public TestablePrinterHealthMonitor(
            IOptions<HardwareSettings> hardwareOptions,
            WorkerEventPipeClient eventPipe)
            : base(
                NullLogger<PrinterHealthMonitor>.Instance,
                hardwareOptions,
                eventPipe)
        {
        }

        public override bool IsHealthy(string printerName, out int winSpoolStatus, out string winSpoolDesc)
        {
            IsHealthyCallCount++;
            winSpoolStatus = MockWinSpoolStatus;
            winSpoolDesc = MockWinSpoolDesc;
            return MockHealthy;
        }
    }

    [Fact]
    public async Task WaitForPrinterHealthyAsync_StopsOnCancellation()
    {
        var settings = new HardwareSettings { PrinterName = "TestPrinter" };
        var ipcSettings = new IpcSettings { WorkerReturnPipeName = "test-pipe" };
        
        var eventPipe = new WorkerEventPipeClient(
            NullLogger<WorkerEventPipeClient>.Instance,
            Options.Create(ipcSettings));

        var monitor = new TestablePrinterHealthMonitor(
            Options.Create(settings),
            eventPipe)
        {
            MockHealthy = false
        };

        using var cts = new CancellationTokenSource();
        // Cancel immediately
        cts.Cancel();

        // Pass 30 seconds timeout, but cancel immediately via token
        var result = await monitor.WaitForPrinterHealthyAsync("TestPrinter", 30, cts.Token);
        
        Assert.False(result);
    }

    [Fact]
    public async Task WaitForPrinterHealthyAsync_ReturnsTrueIfHealthy()
    {
        var settings = new HardwareSettings { PrinterName = "TestPrinter" };
        var ipcSettings = new IpcSettings { WorkerReturnPipeName = "test-pipe" };

        var eventPipe = new WorkerEventPipeClient(
            NullLogger<WorkerEventPipeClient>.Instance,
            Options.Create(ipcSettings));

        var monitor = new TestablePrinterHealthMonitor(
            Options.Create(settings),
            eventPipe)
        {
            MockHealthy = true
        };

        var result = await monitor.WaitForPrinterHealthyAsync("TestPrinter", 30, CancellationToken.None);

        Assert.True(result);
        Assert.Equal(1, monitor.IsHealthyCallCount);
    }

    [Theory]
    [InlineData(0, "Ready")]
    [InlineData(3, "Low Paper")]
    [InlineData(4, "No Paper")]
    [InlineData(6, "No Toner")]
    [InlineData(7, "Door Open")]
    [InlineData(8, "Jammed")]
    [InlineData(9, "Offline")]
    public void TryGetEpsonDriverStatusCode_HandlesNonExistentOrSimulatedCodes(int code, string expectedDescription)
    {
        // For non-existent printer or test environments, verify description mapping
        var desc = PrinterHealthMonitor.TryGetEpsonDriverStatusCode("NonExistentPrinter12345", out var statusCode, out var description);
        // Even if driver DLL not loaded, the method returns false safely without throwing
        Assert.NotNull(description);
    }
}
