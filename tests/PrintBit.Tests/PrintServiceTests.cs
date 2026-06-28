using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;

namespace PrintBit.Tests;

public class PrintServiceTests
{
    [Fact]
    public async Task ProcessSuccessButVerificationFailure_ReturnsFailure()
    {
        var sut = new StubPrintService(
            new PrintJobResult
            {
                Success = true,
                ProcessSucceeded = true,
                Message = "process ok",
                ExitCode = 0
            },
            verificationResult: (false, "No spooler job observed"));

        var result = await sut.PrintAsync(
            new PrintJobRequest
            {
                FilePath = @"C:\PrintBit\sample.pdf",
                PrinterName = "EPSON L5290 Series"
            });

        Assert.False(result.Success);
        Assert.Equal(PrintFailureStage.SpoolerVerification, result.FailureStage);
    }

    [Fact]
    public async Task VerificationTimeout_ReturnsFailure()
    {
        var sut = new StubPrintService(
            new PrintJobResult
            {
                Success = true,
                ProcessSucceeded = true,
                Message = "process ok",
                ExitCode = 0
            },
            verificationResult: (false, "Spooler job did not clear before timeout"));

        var result = await sut.PrintAsync(
            new PrintJobRequest
            {
                FilePath = @"C:\PrintBit\sample.pdf",
                PrinterName = "EPSON L5290 Series"
            });

        Assert.False(result.Success);
        Assert.Equal(PrintFailureStage.SpoolerVerification, result.FailureStage);
        Assert.Contains("timeout", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPrintProcess_FormatsArgumentsCorrectly()
    {
        var request = new PrintJobRequest
        {
            FilePath = @"C:\PrintBit\sample.pdf",
            PrinterName = "MyPrinter",
            Settings = new PrintJobSettings
            {
                Copies = 2,
                Color = true,
                PageRange = "1-3",
                Orientation = "landscape"
            }
        };

        using var process = PrintService.BuildPrintProcess("SumatraPDF.exe", request);

        Assert.Contains("-print-settings \"2x,color,1-3,landscape\"", process.StartInfo.Arguments);
    }

    [Fact]
    public void BuildPrintProcess_DefaultsToMonochromeAnd1Copy()
    {
        var request = new PrintJobRequest
        {
            FilePath = @"C:\PrintBit\sample.pdf",
            PrinterName = "MyPrinter",
            Settings = new PrintJobSettings()
        };

        using var process = PrintService.BuildPrintProcess("SumatraPDF.exe", request);

        Assert.Contains("-print-settings \"1x,monochrome\"", process.StartInfo.Arguments);
    }

    [Fact]
    public async Task HardwareError_PaperOut_ReturnsHardwareErrorFailure()
    {
        var sut = new StubPrintService(
            new PrintJobResult
            {
                Success = true,
                ProcessSucceeded = true,
                Message = "process ok",
                ExitCode = 0
            },
            verificationResult: (false, "Printer hardware error: No Paper (code 4)"));

        var result = await sut.PrintAsync(
            new PrintJobRequest
            {
                FilePath = @"C:\PrintBit\sample.pdf",
                PrinterName = "EPSON L5290 Series"
            });

        Assert.False(result.Success);
        Assert.Equal(PrintFailureStage.HardwareError, result.FailureStage);
        Assert.True(result.ProcessSucceeded);
        Assert.Contains("No Paper", result.Message);
    }

    [Fact]
    public async Task HardwareError_Jammed_ReturnsHardwareErrorFailure()
    {
        var sut = new StubPrintService(
            new PrintJobResult
            {
                Success = true,
                ProcessSucceeded = true,
                Message = "process ok",
                ExitCode = 0
            },
            verificationResult: (false, "Printer hardware error: Jammed (code 8)"));

        var result = await sut.PrintAsync(
            new PrintJobRequest
            {
                FilePath = @"C:\PrintBit\sample.pdf",
                PrinterName = "EPSON L5290 Series"
            });

        Assert.False(result.Success);
        Assert.Equal(PrintFailureStage.HardwareError, result.FailureStage);
        Assert.True(result.ProcessSucceeded);
        Assert.Contains("Jammed", result.Message);
    }

    [Fact]
    public async Task HardwareError_JobErrorFlags_ReturnsHardwareErrorFailure()
    {
        var sut = new StubPrintService(
            new PrintJobResult
            {
                Success = true,
                ProcessSucceeded = true,
                Message = "process ok",
                ExitCode = 0
            },
            verificationResult: (false, "Print job error detected (StatusMask=0x42, JobStatus=Error)"));

        var result = await sut.PrintAsync(
            new PrintJobRequest
            {
                FilePath = @"C:\PrintBit\sample.pdf",
                PrinterName = "EPSON L5290 Series"
            });

        Assert.False(result.Success);
        Assert.Equal(PrintFailureStage.HardwareError, result.FailureStage);
        Assert.True(result.ProcessSucceeded);
        Assert.Contains("StatusMask", result.Message);
    }

    private sealed class StubPrintService : PrintService
    {
        private readonly PrintJobResult _processResult;

        private readonly (bool Success, string Message) _verificationResult;

        public StubPrintService(
            PrintJobResult processResult,
            (bool Success, string Message) verificationResult)
            : base(
                NullLogger<PrintService>.Instance,
                Options.Create(new HardwareSettings()),
                new FakeRecoveryService(),
                new PrintHealthCoordinator())
        {
            _processResult = processResult;
            _verificationResult = verificationResult;
        }

        protected override bool PathExists(string path)
        {
            return true;
        }

        protected override string GetSumatraExecutablePath()
        {
            return "SumatraPDF.exe";
        }

        protected override Task<PrintJobResult> ExecutePrintProcessAsync(
            PrintJobRequest request,
            string sumatraPath,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_processResult);
        }

        protected override Task<(bool Success, string Message, string? SpoolerJobId)> VerifySpoolerLifecycleAsync(
            string printerName,
            string expectedDocument,
            CancellationToken cancellationToken)
        {
            return Task.FromResult((_verificationResult.Success, _verificationResult.Message, (string?)null));
        }

        protected override (bool HasError, int ErrorCode, string Description) CheckPrinterErrorState(
            string printerName)
        {
            return (false, 0, "No Error");
        }
    }
}
