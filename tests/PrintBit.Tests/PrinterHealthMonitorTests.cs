using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Infrastructure.Windows.PrinterMonitoring;
using PrintBit.Shared.Configurations;
using Xunit;

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
            IWorkerEventPipeClient eventPipe)
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

    private sealed class WmiStatusMonitor : PrinterHealthMonitor
    {
        private readonly int _detectedErrorState;
        private readonly int _extendedPrinterStatus;
        private readonly bool _isOffline;

        public WmiStatusMonitor(
            IOptions<HardwareSettings> hardwareOptions,
            IWorkerEventPipeClient eventPipe,
            int detectedErrorState = 0,
            int extendedPrinterStatus = 3,
            bool isOffline = false)
            : base(
                NullLogger<PrinterHealthMonitor>.Instance,
                hardwareOptions,
                eventPipe)
        {
            _detectedErrorState = detectedErrorState;
            _extendedPrinterStatus = extendedPrinterStatus;
            _isOffline = isOffline;
        }

        protected override bool TryReadMonitorStatus(
            string printerName,
            out bool isOffline,
            out int detectedErrorState,
            out int extendedPrinterStatus)
        {
            isOffline = _isOffline;
            detectedErrorState = _detectedErrorState;
            extendedPrinterStatus = _extendedPrinterStatus;
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
    public async Task HasFatalHardwareError_WmiNoPaper_ReturnsTrue()
    {
        var eventPipe = new Mock<IWorkerEventPipeClient>();
        var monitor = new WmiStatusMonitor(
            Options.Create(new HardwareSettings { PrinterName = "EPSON L5290 Series" }),
            eventPipe.Object,
            detectedErrorState: 4);

        // Monitor once so _fatalErrorCode gets updated
        await monitor.MonitorOnceAsync(CancellationToken.None);
        var hasFatal = monitor.HasFatalHardwareError("EPSON L5290 Series", out var code, out var msg);

        Assert.True(hasFatal);
        Assert.Equal(4, code);
        Assert.Contains("No Paper", msg);
    }

    [Fact]
    public async Task MonitorPrinterAsync_WmiNoPaper_EmitsPrinterError()
    {
        var capturedEvents = new List<WorkerPrintEvent>();
        var eventPipe = new Mock<IWorkerEventPipeClient>();
        eventPipe
            .Setup(pipe => pipe.SendAsync(It.IsAny<WorkerPrintEvent>(), It.IsAny<CancellationToken>()))
            .Callback<WorkerPrintEvent, CancellationToken>((evt, _) => capturedEvents.Add(evt))
            .ReturnsAsync(true);

        var monitor = new WmiStatusMonitor(
            Options.Create(new HardwareSettings { PrinterName = "EPSON L5290 Series" }),
            eventPipe.Object,
            detectedErrorState: 4);

        await monitor.MonitorOnceAsync(CancellationToken.None);

        var printerError = Assert.Single(capturedEvents);
        Assert.Equal(WorkerPrintEventType.PrinterError, printerError.Type);
        Assert.Equal("EPSON L5290 Series", printerError.PrinterName);
        Assert.Equal("hardware_error", printerError.FailureStage);
        Assert.Contains("No Paper", printerError.Message);
    }
}
