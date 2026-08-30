using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;
using PrintBit.Shared.Printing;

namespace PrintBit.Tests;

public class JobOrchestratorTests
{
    [Fact]
    public async Task ProcessJobAsync_ThreePagesTwoCopies_DispatchesOriginalPdfPerCopy()
    {
        var tempPdf = CreatePdf(pageCount: 3);
        try
        {
            var healthMock = CreateHealthyMonitor();
            var dispatches = new List<(string FilePath, int CopyNumber, int[] Pages)>();
            var printerMock = new Mock<IDocumentPrinter>();
            printerMock.Setup(p => p.PrintDocumentAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<IReadOnlyList<int>>(),
                    It.IsAny<PrintJobSettings>(),
                    It.IsAny<Func<int, int, Task>>(),
                    It.IsAny<Func<string, Task>>(),
                    It.IsAny<Func<Task>>(),
                    It.IsAny<CancellationToken>()))
                .Callback((
                    string filePath,
                    string _,
                    int copyNumber,
                    IReadOnlyList<int> pages,
                    PrintJobSettings _,
                    Func<int, int, Task> _,
                    Func<string, Task> _,
                    Func<Task> _,
                    CancellationToken _) =>
                    dispatches.Add((filePath, copyNumber, pages.ToArray())))
                .ReturnsAsync(new DocumentPrintResult
                {
                    State = PagePrintState.Completed,
                    PagesPrinted = 3,
                    TotalPages = 3,
                    PageCountConfidence = "confirmed"
                });

            var events = new List<WorkerPrintEvent>();
            var eventPipeMock = CreateEventPipe(events);
            var sut = CreateSut(healthMock.Object, printerMock.Object, eventPipeMock.Object);

            var result = await sut.ProcessJobAsync(
                new PrintJobRequest
                {
                    FilePath = tempPdf,
                    PrinterName = "TestPrinter",
                    Settings = new PrintJobSettings { Copies = 2 }
                },
                Path.ChangeExtension(tempPdf, ".json"),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(6, result.PagesPrinted);
            Assert.Equal(6, result.TotalPages);
            Assert.Equal("confirmed", result.PageCountConfidence);
            Assert.Equal(
                [WorkerPrintEventType.PrintStarted, WorkerPrintEventType.PrintSucceeded],
                events.Select(evt => evt.Type));
            Assert.Equal(2, dispatches.Count);
            Assert.All(dispatches, dispatch => Assert.Equal(tempPdf, dispatch.FilePath));
            Assert.Equal([1, 2], dispatches.Select(dispatch => dispatch.CopyNumber));
            Assert.All(dispatches, dispatch => Assert.Equal([1, 2, 3], dispatch.Pages));

            var terminal = Assert.Single(events, evt =>
                evt.Type is WorkerPrintEventType.PrintSucceeded or WorkerPrintEventType.PrintFailed);
            Assert.Equal(WorkerPrintEventType.PrintSucceeded, terminal.Type);
            Assert.Equal(6, terminal.CompletedCount);
            Assert.Equal(6, terminal.TotalExpected);
            Assert.Equal("confirmed", terminal.PageCountConfidence);
            Assert.All(terminal.Pages!, page => Assert.Equal("completed", page.State));
        }
        finally
        {
            File.Delete(tempPdf);
        }
    }

    [Fact]
    public async Task ProcessJobAsync_SecondCopyPartiallyFails_EmitsFailedBestEffortResult()
    {
        var tempPdf = CreatePdf(pageCount: 3);
        try
        {
            var healthMock = CreateHealthyMonitor();
            var printerMock = new Mock<IDocumentPrinter>();
            printerMock.Setup(p => p.PrintDocumentAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<IReadOnlyList<int>>(),
                    It.IsAny<PrintJobSettings>(),
                    It.IsAny<Func<int, int, Task>>(),
                    It.IsAny<Func<string, Task>>(),
                    It.IsAny<Func<Task>>(),
                    It.IsAny<CancellationToken>()))
                .Returns((
                    string _,
                    string _,
                    int copyNumber,
                    IReadOnlyList<int> _,
                    PrintJobSettings _,
                    Func<int, int, Task> _,
                    Func<string, Task> _,
                    Func<Task> _,
                    CancellationToken _) => Task.FromResult(copyNumber == 1
                        ? new DocumentPrintResult
                        {
                            State = PagePrintState.Completed,
                            PagesPrinted = 3,
                            TotalPages = 3,
                            PageCountConfidence = "confirmed"
                        }
                        : new DocumentPrintResult
                        {
                            State = PagePrintState.Failed,
                            FailureStage = PrintFailureStage.HardwareError,
                            ErrorMessage = "Out of paper",
                            PagesPrinted = 1,
                            TotalPages = 3,
                            PageCountConfidence = "best_effort"
                        }));

            var events = new List<WorkerPrintEvent>();
            var eventPipeMock = CreateEventPipe(events);
            var sut = CreateSut(healthMock.Object, printerMock.Object, eventPipeMock.Object);

            var result = await sut.ProcessJobAsync(
                new PrintJobRequest
                {
                    FilePath = tempPdf,
                    PrinterName = "TestPrinter",
                    Settings = new PrintJobSettings { Copies = 2 }
                },
                Path.ChangeExtension(tempPdf, ".json"),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(PrintFailureStage.HardwareError, result.FailureStage);
            Assert.Equal(4, result.PagesPrinted);
            Assert.Equal(6, result.TotalPages);
            Assert.Equal("best_effort", result.PageCountConfidence);

            var terminal = Assert.Single(events, evt =>
                evt.Type is WorkerPrintEventType.PrintSucceeded or WorkerPrintEventType.PrintFailed);
            Assert.Equal(WorkerPrintEventType.PrintFailed, terminal.Type);
            Assert.Equal("partially_completed", terminal.Outcome);
            Assert.Equal(4, terminal.CompletedCount);
            Assert.Equal(1, terminal.FailedCount);
            Assert.Equal(1, terminal.CancelledCount);
            Assert.Equal("best_effort", terminal.PageCountConfidence);
            Assert.Equal(
                ["completed", "failed", "cancelled"],
                terminal.Pages!.Where(page => page.Copy == 2).Select(page => page.State));
        }
        finally
        {
            File.Delete(tempPdf);
        }
    }

    [Fact]
    public async Task ProcessJobAsync_InvalidFilename_ReturnsValidationFailure()
    {
        var sut = CreateSut(
            Mock.Of<IPrinterHealthMonitor>(),
            Mock.Of<IDocumentPrinter>(),
            Mock.Of<IWorkerEventPipeClient>());

        var result = await sut.ProcessJobAsync(
            new PrintJobRequest
            {
                FilePath = "invalidfilename.pdf",
                PrinterName = "TestPrinter"
            },
            "invalidfilename.json",
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(PrintFailureStage.Validation, result.FailureStage);
    }

    [Fact]
    public async Task ProcessJobAsync_PrinterUnhealthyAtPreflight_EmitsJobPausedEvent()
    {
        var tempPdf = CreatePdf(pageCount: 1);
        try
        {
            var healthMock = new Mock<IPrinterHealthMonitor>();
            var status = 4;
            var description = "Epson hardware status: No Paper";
            healthMock.Setup(h => h.IsHealthy("TestPrinter", out status, out description))
                .Returns(false);

            var events = new List<WorkerPrintEvent>();
            var eventPipeMock = CreateEventPipe(events);
            var sut = new JobOrchestrator(
                NullLogger<JobOrchestrator>.Instance,
                Options.Create(new HardwareSettings
                {
                    QpdfPath = "qpdf.exe",
                    PrinterName = "TestPrinter",
                    PauseTimeoutMinutes = 0
                }),
                Mock.Of<IDocumentPrinter>(),
                healthMock.Object,
                eventPipeMock.Object);

            var result = await sut.ProcessJobAsync(
                new PrintJobRequest
                {
                    FilePath = tempPdf,
                    PrinterName = "TestPrinter"
                },
                Path.ChangeExtension(tempPdf, ".json"),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(PrintFailureStage.HardwareError, result.FailureStage);
            Assert.Contains(events, evt => evt.Type == WorkerPrintEventType.JobPaused);
            var pausedEvent = events.First(evt => evt.Type == WorkerPrintEventType.JobPaused);
            Assert.Equal("tx-123", pausedEvent.TransactionId);
            Assert.Equal("spool-456", pausedEvent.SpoolerCorrelationKey);
            Assert.Contains("No Paper", pausedEvent.Message);
        }
        finally
        {
            File.Delete(tempPdf);
        }
    }

    [Fact]
    public async Task ProcessJobAsync_DocumentPrinterTriggersPausedAndResumed_EmitsEvents()
    {
        var tempPdf = CreatePdf(pageCount: 2);
        try
        {
            var healthMock = CreateHealthyMonitor();
            var printerMock = new Mock<IDocumentPrinter>();
            printerMock.Setup(p => p.PrintDocumentAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<IReadOnlyList<int>>(),
                    It.IsAny<PrintJobSettings>(),
                    It.IsAny<Func<int, int, Task>>(),
                    It.IsAny<Func<string, Task>>(),
                    It.IsAny<Func<Task>>(),
                    It.IsAny<CancellationToken>()))
                .Callback(async (
                    string _,
                    string _,
                    int _,
                    IReadOnlyList<int> _,
                    PrintJobSettings _,
                    Func<int, int, Task> onProgress,
                    Func<string, Task> onPaused,
                    Func<Task> onResumed,
                    CancellationToken _) =>
                {
                    await onProgress(1, 2);
                    await onPaused("Paper Out");
                    await onResumed();
                    await onProgress(2, 2);
                })
                .ReturnsAsync(new DocumentPrintResult
                {
                    State = PagePrintState.Completed,
                    PagesPrinted = 2,
                    TotalPages = 2,
                    PageCountConfidence = "confirmed"
                });

            var events = new List<WorkerPrintEvent>();
            var eventPipeMock = CreateEventPipe(events);
            var sut = CreateSut(healthMock.Object, printerMock.Object, eventPipeMock.Object);

            var result = await sut.ProcessJobAsync(
                new PrintJobRequest
                {
                    FilePath = tempPdf,
                    PrinterName = "TestPrinter"
                },
                Path.ChangeExtension(tempPdf, ".json"),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Contains(events, evt => evt.Type == WorkerPrintEventType.PrintProgress);
            Assert.Contains(events, evt => evt.Type == WorkerPrintEventType.JobPaused && evt.Message == "Paper Out");
            Assert.Contains(events, evt => evt.Type == WorkerPrintEventType.JobResumed);
            Assert.Contains(events, evt => evt.Type == WorkerPrintEventType.PrintSucceeded);
        }
        finally
        {
            File.Delete(tempPdf);
        }
    }

    private static JobOrchestrator CreateSut(
        IPrinterHealthMonitor healthMonitor,
        IDocumentPrinter documentPrinter,
        IWorkerEventPipeClient eventPipe)
    {
        return new JobOrchestrator(
            NullLogger<JobOrchestrator>.Instance,
            Options.Create(new HardwareSettings
            {
                QpdfPath = "qpdf.exe",
                PrinterName = "TestPrinter",
                PauseTimeoutMinutes = 1
            }),
            documentPrinter,
            healthMonitor,
            eventPipe);
    }

    private static Mock<IPrinterHealthMonitor> CreateHealthyMonitor()
    {
        var monitor = new Mock<IPrinterHealthMonitor>();
        var status = 0;
        var description = "OK";
        monitor.Setup(h => h.IsHealthy("TestPrinter", out status, out description))
            .Returns(true);
        return monitor;
    }

    private static Mock<IWorkerEventPipeClient> CreateEventPipe(List<WorkerPrintEvent> events)
    {
        var pipe = new Mock<IWorkerEventPipeClient>();
        pipe.Setup(p => p.SendAsync(It.IsAny<WorkerPrintEvent>(), It.IsAny<CancellationToken>()))
            .Callback((WorkerPrintEvent evt, CancellationToken _) => events.Add(evt))
            .ReturnsAsync(true);
        return pipe;
    }

    private static string CreatePdf(int pageCount)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"tx-123_spool-456_{Guid.NewGuid():N}.pdf");
        File.WriteAllText(path, $"%PDF-1.7\n/Type /Pages /Count {pageCount}");
        return path;
    }
}
