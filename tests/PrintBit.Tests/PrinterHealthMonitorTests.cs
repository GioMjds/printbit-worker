using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
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

    private sealed class EpsonStatusMonitor : PrinterHealthMonitor
    {
        private readonly int _epsonStatusCode;

        public EpsonStatusMonitor(
            IOptions<HardwareSettings> hardwareOptions,
            IWorkerEventPipeClient eventPipe,
            int epsonStatusCode)
            : base(
                NullLogger<PrinterHealthMonitor>.Instance,
                hardwareOptions,
                eventPipe)
        {
            _epsonStatusCode = epsonStatusCode;
        }

        protected override bool TryReadMonitorStatus(
            string printerName,
            out bool isOffline,
            out int detectedErrorState,
            out int extendedPrinterStatus)
        {
            isOffline = false;
            detectedErrorState = 0;
            extendedPrinterStatus = 3;
            return true;
        }

        protected override bool TryGetEpsonDriverStatusCode(
            string printerName,
            out int statusCode,
            out string description)
        {
            statusCode = _epsonStatusCode;
            description = "No Paper";
            return true;
        }

        public Task MonitorOnceAsync(CancellationToken cancellationToken) =>
            MonitorPrinterAsync(cancellationToken);
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

    [Fact]
    public async Task MonitorPrinterAsync_EpsonDriverNoPaper_EmitsPrinterError()
    {
        var capturedEvents = new List<WorkerPrintEvent>();
        var eventPipe = new Mock<IWorkerEventPipeClient>();
        eventPipe
            .Setup(pipe => pipe.SendAsync(It.IsAny<WorkerPrintEvent>(), It.IsAny<CancellationToken>()))
            .Callback<WorkerPrintEvent, CancellationToken>((evt, _) => capturedEvents.Add(evt))
            .ReturnsAsync(true);

        var monitor = new EpsonStatusMonitor(
            Options.Create(new HardwareSettings { PrinterName = "EPSON L5290 Series" }),
            eventPipe.Object,
            epsonStatusCode: 4);

        await monitor.MonitorOnceAsync(CancellationToken.None);

        var printerError = Assert.Single(capturedEvents);
        Assert.Equal(WorkerPrintEventType.PrinterError, printerError.Type);
        Assert.Equal("EPSON L5290 Series", printerError.PrinterName);
        Assert.Equal("hardware_error", printerError.FailureStage);
        Assert.Contains("No Paper", printerError.Message);
    }

    [Fact]
    public async Task MonitorPrinterAsync_EpsonDriverNoPaper_UsesActiveJobCorrelation()
    {
        var capturedEvents = new List<WorkerPrintEvent>();
        var eventPipe = new Mock<IWorkerEventPipeClient>();
        eventPipe
            .Setup(pipe => pipe.SendAsync(It.IsAny<WorkerPrintEvent>(), It.IsAny<CancellationToken>()))
            .Callback<WorkerPrintEvent, CancellationToken>((evt, _) => capturedEvents.Add(evt))
            .ReturnsAsync(true);

        var monitor = new EpsonStatusMonitor(
            Options.Create(new HardwareSettings { PrinterName = "EPSON L5290 Series" }),
            eventPipe.Object,
            epsonStatusCode: 4);

        using var activeJob = monitor.BeginActivePrintJob(
            "tx-paper-out",
            "spool-paper-out");
        await monitor.MonitorOnceAsync(CancellationToken.None);

        var printerError = Assert.Single(capturedEvents);
        Assert.Equal("tx-paper-out", printerError.TransactionId);
        Assert.Equal("spool-paper-out", printerError.SpoolerCorrelationKey);
    }
}
