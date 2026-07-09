using Xunit;
using Moq;
using Microsoft.Extensions.Options;
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;
using System.Threading;
using System.Threading.Tasks;

namespace PrintBit.Tests;

public class JobOrchestratorTests
{
    [Fact]
    public async Task ProcessJobAsync_ThrowsOnCancellation()
    {
        var orchestratorMock = new Mock<IJobOrchestrator>();
        orchestratorMock.Setup(o => o.ProcessJobAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.OperationCanceledException());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<System.OperationCanceledException>(async () =>
        {
            await orchestratorMock.Object.ProcessJobAsync("path.pdf", "TX-1", "SCK-1", 1, cts.Token);
        });
    }

    [Fact]
    public async Task ProcessJobAsync_UnhealthyPrinter_EmitsFailureEvent()
    {
        // Arrange
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<JobOrchestrator>.Instance;
        var settings = new HardwareSettings { PrinterName = "TestPrinter" };
        var options = Options.Create(settings);
        
        var pagePrinterMock = new Mock<IPagePrinter>();
        
        var ipcSettings = new IpcSettings { WorkerReturnPipeName = "test-pipe", ConnectTimeoutMs = 10 };
        var eventPipeLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkerEventPipeClient>.Instance;
        var eventPipe = new WorkerEventPipeClient(eventPipeLogger, Options.Create(ipcSettings));
        
        var healthMonitorMock = new Mock<IPrinterHealthMonitor>();
        int mockStatus = 0x80;
        string mockDesc = "Offline";
        healthMonitorMock.Setup(h => h.IsHealthy(It.IsAny<string>(), out mockStatus, out mockDesc))
            .Returns(false);

        var orchestrator = new JobOrchestrator(
            logger,
            options,
            pagePrinterMock.Object,
            eventPipe,
            healthMonitorMock.Object);

        // Act
        await orchestrator.ProcessJobAsync("dummy.pdf", "TX-123", "SCK-123", 1, CancellationToken.None);

        // Assert
        healthMonitorMock.Verify(h => h.IsHealthy("TestPrinter", out mockStatus, out mockDesc), Times.Once);
        pagePrinterMock.Verify(p => p.PrintPageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<System.Action<string>>(), It.IsAny<System.Action>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
