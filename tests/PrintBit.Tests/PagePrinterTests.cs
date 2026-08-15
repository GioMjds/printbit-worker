using Xunit;
using Moq;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Printing;
using PrintBit.Shared.Configurations;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PrintBit.Tests;

public class PagePrinterTests
{
    private static string GetDummyExecutablePath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "PrintBitTests");
        Directory.CreateDirectory(tempDir);
        var exePath = Path.Combine(tempDir, "dummy_sumatra.exe");
        if (File.Exists(exePath)) return exePath;

        var sourcePath = Path.Combine(tempDir, "dummy_sumatra.cs");
        File.WriteAllText(sourcePath, "class Program { static void Main() {} }");

        var cscPath = @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe";
        if (!File.Exists(cscPath))
        {
            throw new FileNotFoundException("csc.exe not found under framework directory", cscPath);
        }

        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = cscPath,
            Arguments = $"/out:\"{exePath}\" \"{sourcePath}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });
        process?.WaitForExit();
        return exePath;
    }

    [Fact]
    public async Task PrintPageAsync_FileDoesNotExist_ReturnsFailedState()
    {
        var healthMock = new Mock<IPrinterHealthMonitor>();
        var settings = new HardwareSettings { SumatraPath = "Sumatra.exe" };
        var options = Microsoft.Extensions.Options.Options.Create(settings);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<PagePrinter>.Instance;

        var sut = new PagePrinter(logger, options, healthMock.Object);

        var result = await sut.PrintPageAsync(
            "nonexistent.pdf",
            "PrinterName",
            0,
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(PagePrintState.Failed, result.State);
        Assert.Equal(PrintFailureStage.Validation, result.FailureStage);
    }

    [Fact]
    public async Task PrintPageAsync_FastSuccess_ReturnsCompleted()
    {
        var dummyExe = GetDummyExecutablePath();
        var tempPdf = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pdf");
        File.WriteAllText(tempPdf, "%PDF");

        try
        {
            var healthMock = new Mock<IPrinterHealthMonitor>();
            healthMock.Setup(h => h.QueryJobStatus(It.IsAny<string>(), It.IsAny<string>()))
                      .Returns((false, 0u, "", 0, 0, null));
            
            int winSpoolStatus = 0;
            string winSpoolDesc = "OK";
            healthMock.Setup(h => h.IsHealthy(It.IsAny<string>(), out winSpoolStatus, out winSpoolDesc))
                      .Returns(true);

            var settings = new HardwareSettings { SumatraPath = dummyExe, PrintTimeoutSeconds = 5, PostClearGuardDelaySeconds = 0 };
            var sut = new PagePrinter(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PagePrinter>.Instance,
                Microsoft.Extensions.Options.Options.Create(settings),
                healthMock.Object);

            var result = await sut.PrintPageAsync(
                tempPdf,
                "TestPrinter",
                0,
                _ => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                CancellationToken.None);

            Assert.Equal(PagePrintState.Completed, result.State);
        }
        finally
        {
            try { File.Delete(tempPdf); } catch {}
        }
    }

    [Fact]
    public async Task PrintPageAsync_ActiveSuccess_ReturnsCompletedAfterGuard()
    {
        var dummyExe = GetDummyExecutablePath();
        var tempPdf = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pdf");
        File.WriteAllText(tempPdf, "%PDF");

        try
        {
            var healthMock = new Mock<IPrinterHealthMonitor>();
            int pollCount = 0;
            healthMock.Setup(h => h.QueryJobStatus(It.IsAny<string>(), It.IsAny<string>()))
                      .Returns(() =>
                      {
                          pollCount++;
                          if (pollCount == 1)
                          {
                              return (true, 0x10u, "Printing", 0, 1, "123");
                          }
                          return (false, 0u, "", 0, 0, null);
                      });

            int errorCode = 0;
            string errorMessage = "";
            healthMock.Setup(h => h.HasFatalHardwareError(It.IsAny<string>(), out errorCode, out errorMessage))
                      .Returns(false);

            var settings = new HardwareSettings { SumatraPath = dummyExe, PrintTimeoutSeconds = 5, PostClearGuardDelaySeconds = 0 };
            var sut = new PagePrinter(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PagePrinter>.Instance,
                Microsoft.Extensions.Options.Options.Create(settings),
                healthMock.Object);

            var result = await sut.PrintPageAsync(
                tempPdf,
                "TestPrinter",
                0,
                _ => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                CancellationToken.None);

            Assert.Equal(PagePrintState.Completed, result.State);
        }
        finally
        {
            try { File.Delete(tempPdf); } catch {}
        }
    }

    [Fact]
    public async Task PrintPageAsync_Cancellation_ReturnsCancelledState()
    {
        var dummyExe = GetDummyExecutablePath();
        var tempPdf = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pdf");
        File.WriteAllText(tempPdf, "%PDF");

        try
        {
            var healthMock = new Mock<IPrinterHealthMonitor>();
            int pollCount = 0;
            healthMock.Setup(h => h.QueryJobStatus(It.IsAny<string>(), It.IsAny<string>()))
                      .Returns(() =>
                      {
                          pollCount++;
                          if (pollCount == 1)
                          {
                              // Job exists but is in error state, not printing.
                              return (true, 0x2u, "Error", 0, 1, "123");
                          }
                          return (false, 0u, "", 0, 0, null);
                      });

            var settings = new HardwareSettings { SumatraPath = dummyExe, PrintTimeoutSeconds = 5, PostClearGuardDelaySeconds = 0 };
            var sut = new PagePrinter(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PagePrinter>.Instance,
                Microsoft.Extensions.Options.Options.Create(settings),
                healthMock.Object);

            var result = await sut.PrintPageAsync(
                tempPdf,
                "TestPrinter",
                0,
                _ => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                CancellationToken.None);

            Assert.Equal(PagePrintState.Cancelled, result.State);
        }
        finally
        {
            try { File.Delete(tempPdf); } catch {}
        }
    }

    [Fact]
    public async Task PrintPageAsync_PostClearError_ReturnsHardwareError()
    {
        var dummyExe = GetDummyExecutablePath();
        var tempPdf = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pdf");
        File.WriteAllText(tempPdf, "%PDF");

        try
        {
            var healthMock = new Mock<IPrinterHealthMonitor>();
            int pollCount = 0;
            healthMock.Setup(h => h.QueryJobStatus(It.IsAny<string>(), It.IsAny<string>()))
                      .Returns(() =>
                      {
                          pollCount++;
                          if (pollCount == 1)
                          {
                              return (true, 0x10u, "Printing", 0, 1, "123");
                          }
                          return (false, 0u, "", 0, 0, null);
                      });

            int errorCode = 5;
            string errorMessage = "Paper Jam";
            healthMock.Setup(h => h.HasFatalHardwareError(It.IsAny<string>(), out errorCode, out errorMessage))
                      .Returns(true);

            var settings = new HardwareSettings { SumatraPath = dummyExe, PrintTimeoutSeconds = 5, PostClearGuardDelaySeconds = 0 };
            var sut = new PagePrinter(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PagePrinter>.Instance,
                Microsoft.Extensions.Options.Options.Create(settings),
                healthMock.Object);

            var result = await sut.PrintPageAsync(
                tempPdf,
                "TestPrinter",
                0,
                _ => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                CancellationToken.None);

            Assert.Equal(PagePrintState.Failed, result.State);
            Assert.Equal(PrintFailureStage.HardwareError, result.FailureStage);
            Assert.Contains("Paper Jam", result.ErrorMessage);
        }
        finally
        {
            try { File.Delete(tempPdf); } catch {}
        }
    }
}
