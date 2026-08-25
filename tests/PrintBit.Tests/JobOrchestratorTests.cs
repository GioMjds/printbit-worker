using Xunit;
using Moq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;
using PrintBit.Shared.Printing;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PrintBit.Tests;

public class JobOrchestratorTests
{
    private class TestableJobOrchestrator : JobOrchestrator
    {
        public bool SimulateSplitFailure { get; set; }

        public TestableJobOrchestrator(
            IPrinterHealthMonitor healthMonitor,
            IPagePrinter pagePrinter,
            WorkerEventPipeClient eventPipe)
            : base(
                NullLogger<JobOrchestrator>.Instance,
                Options.Create(new HardwareSettings { QpdfPath = "qpdf.exe", PrinterName = "TestPrinter" }),
                pagePrinter,
                healthMonitor,
                eventPipe)
        {
        }

        protected override Task SplitPdfPagesAsync(string filePath, string workDir, CancellationToken cancellationToken)
        {
            if (SimulateSplitFailure)
            {
                throw new InvalidOperationException("Simulated qpdf error");
            }

            // Create mock split files
            File.WriteAllText(Path.Combine(workDir, "page-00001.pdf"), "%PDF");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ProcessJobAsync_InvalidFilename_ReturnsValidationFailure()
    {
        var healthMock = new Mock<IPrinterHealthMonitor>();
        var printerMock = new Mock<IPagePrinter>();
        var orchestrator = new TestableJobOrchestrator(healthMock.Object, printerMock.Object, null!);

        var request = new PrintJobRequest
        {
            FilePath = "invalidfilename.pdf",
            PrinterName = "TestPrinter",
            Settings = new PrintJobSettings { Copies = 1 }
        };

        var result = await orchestrator.ProcessJobAsync(request, "dummy.json", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(PrintFailureStage.Validation, result.FailureStage);
        Assert.Contains("Filename does not match", result.Message);
    }

    [Fact]
    public async Task ProcessJobAsync_PreFlightUnhealthy_EntersPauseWaitAndSucceeds()
    {
        var tempPdf = Path.Combine(Path.GetTempPath(), $"TX-123_SCK-456_{Guid.NewGuid():N}.pdf");
        File.WriteAllText(tempPdf, "%PDF-1.7\n/Type /Pages /Count 1");

        try
        {
            var healthMock = new Mock<IPrinterHealthMonitor>();
            int pollCount = 0;
            // Unhealthy first, then healthy
            healthMock.Setup(h => h.IsHealthy("TestPrinter", out It.Ref<int>.IsAny, out It.Ref<string>.IsAny))
                      .Returns((string p, out int s, out string d) =>
                      {
                          pollCount++;
                          s = 0;
                          d = "OK";
                          return pollCount > 1; // True on second call (during pause wait)
                      });

            var printerMock = new Mock<IPagePrinter>();
            printerMock.Setup(p => p.PrintPageAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<string>(),
                It.IsAny<Func<string, Task>>(),
                It.IsAny<Func<Task>>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PagePrintResult { State = PagePrintState.Completed });

            var orchestrator = new TestableJobOrchestrator(healthMock.Object, printerMock.Object, null!);

            var request = new PrintJobRequest
            {
                FilePath = tempPdf,
                PrinterName = "TestPrinter",
                Settings = new PrintJobSettings { Copies = 1 }
            };

            var result = await orchestrator.ProcessJobAsync(request, "dummy.json", CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(1, result.PagesPrinted);
            printerMock.Verify(p => p.PrintPageAsync(It.IsAny<string>(), It.IsAny<string>(), 0, It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<Func<string, Task>>(), It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            try { File.Delete(tempPdf); } catch { }
        }
    }

    [Fact]
    public async Task ProcessJobAsync_PagePrinterFails_ReturnsFailure()
    {
        var tempPdf = Path.Combine(Path.GetTempPath(), $"TX-123_SCK-456_{Guid.NewGuid():N}.pdf");
        File.WriteAllText(tempPdf, "%PDF-1.7\n/Type /Pages /Count 1");

        try
        {
            var healthMock = new Mock<IPrinterHealthMonitor>();
            healthMock.Setup(h => h.IsHealthy("TestPrinter", out It.Ref<int>.IsAny, out It.Ref<string>.IsAny))
                      .Returns(true);

            var printerMock = new Mock<IPagePrinter>();
            printerMock.Setup(p => p.PrintPageAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<string>(),
                It.IsAny<Func<string, Task>>(),
                It.IsAny<Func<Task>>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PagePrintResult { State = PagePrintState.Failed, FailureStage = PrintFailureStage.HardwareError, ErrorMessage = "Out of paper" });

            var orchestrator = new TestableJobOrchestrator(healthMock.Object, printerMock.Object, null!);

            var request = new PrintJobRequest
            {
                FilePath = tempPdf,
                PrinterName = "TestPrinter",
                Settings = new PrintJobSettings { Copies = 1 }
            };

            var result = await orchestrator.ProcessJobAsync(request, "dummy.json", CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(PrintFailureStage.HardwareError, result.FailureStage);
            Assert.Contains("Out of paper", result.Message);
        }
        finally
        {
            try { File.Delete(tempPdf); } catch { }
        }
    }

    [Fact]
    public async Task ProcessJobAsync_PagePrinterCancelled_ReturnsCancelledOutcome()
    {
        var tempPdf = Path.Combine(Path.GetTempPath(), $"TX-123_SCK-456_{Guid.NewGuid():N}.pdf");
        File.WriteAllText(tempPdf, "%PDF-1.7\n/Type /Pages /Count 1");

        try
        {
            var healthMock = new Mock<IPrinterHealthMonitor>();
            healthMock.Setup(h => h.IsHealthy("TestPrinter", out It.Ref<int>.IsAny, out It.Ref<string>.IsAny))
                      .Returns(true);

            var printerMock = new Mock<IPagePrinter>();
            printerMock.Setup(p => p.PrintPageAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<string>(),
                It.IsAny<Func<string, Task>>(),
                It.IsAny<Func<Task>>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PagePrintResult { State = PagePrintState.Cancelled, FailureStage = PrintFailureStage.SpoolerVerification, ErrorMessage = "User stop" });

            var orchestrator = new TestableJobOrchestrator(healthMock.Object, printerMock.Object, null!);

            var request = new PrintJobRequest
            {
                FilePath = tempPdf,
                PrinterName = "TestPrinter",
                Settings = new PrintJobSettings { Copies = 1 }
            };

            var result = await orchestrator.ProcessJobAsync(request, "dummy.json", CancellationToken.None);

            // PrintJobResult is successful even on user cancellation (Outcome = partially_completed)
            Assert.True(result.Success);
            Assert.Equal(0, result.PagesPrinted);
        }
        finally
        {
            try { File.Delete(tempPdf); } catch { }
        }
    }
}
