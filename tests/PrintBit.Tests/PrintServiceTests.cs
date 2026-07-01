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
                SumatraProcessSucceeded = true,
                Message = "process ok",
                ExitCode = 0
            },
            verificationStage: PrintFailureStage.SpoolerVerification,
            verificationMessage: "No spooler job observed");

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
                SumatraProcessSucceeded = true,
                Message = "process ok",
                ExitCode = 0
            },
            verificationStage: PrintFailureStage.SpoolerVerification,
            verificationMessage: "Spooler job did not clear before timeout");

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

        // ArgumentList is collection-based, so each element is passed verbatim
        // to the OS without shell-style escaping. We assert the token order and
        // content directly.
        var args = process.StartInfo.ArgumentList;
        Assert.Equal("SumatraPDF.exe", process.StartInfo.FileName);
        Assert.Equal("-print-to", args[0]);
        Assert.Equal("MyPrinter", args[1]);
        Assert.Equal("-print-settings", args[2]);
        Assert.Equal("2x,color,1-3,landscape", args[3]);
        Assert.Equal("-silent", args[4]);
        Assert.Equal(@"C:\PrintBit\sample.pdf", args[5]);
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

        var args = process.StartInfo.ArgumentList;
        Assert.Equal("-print-settings", args[2]);
        Assert.Equal("1x,monochrome", args[3]);
    }

    [Theory]
    [MemberData(nameof(HardwareErrorMessages))]
    public async Task HardwareError_Verification_ReturnsHardwareErrorFailure(
        string verificationMessage,
        string expectedSubstring)
    {
        var sut = new StubPrintService(
            new PrintJobResult
            {
                Success = true,
                SumatraProcessSucceeded = true,
                Message = "process ok",
                ExitCode = 0
            },
            verificationStage: PrintFailureStage.HardwareError,
            verificationMessage: verificationMessage);

        var result = await sut.PrintAsync(
            new PrintJobRequest
            {
                FilePath = @"C:\PrintBit\sample.pdf",
                PrinterName = "EPSON L5290 Series"
            });

        Assert.False(result.Success);
        Assert.Equal(PrintFailureStage.HardwareError, result.FailureStage);
        Assert.True(result.SumatraProcessSucceeded);
        Assert.Contains(expectedSubstring, result.Message);
    }

    public static IEnumerable<object[]> HardwareErrorMessages =>
        new List<object[]>
        {
            new object[] { "Printer hardware error: No Paper (code 4)", "No Paper" },
            new object[] { "Printer hardware error: Jammed (code 8)", "Jammed" },
            new object[] { "Print job error detected (StatusMask=0x42, JobStatus=Error)", "StatusMask" },
        };

    private sealed class StubPrintService : PrintService
    {
        private readonly PrintJobResult _processResult;

        private readonly PrintFailureStage _verificationStage;

        private readonly string _verificationMessage;

        public StubPrintService(
            PrintJobResult processResult,
            PrintFailureStage verificationStage,
            string verificationMessage)
            : base(
                NullLogger<PrintService>.Instance,
                Options.Create(new HardwareSettings()),
                new FakeRecoveryService(),
                new PrintHealthCoordinator())
        {
            _processResult = processResult;
            _verificationStage = verificationStage;
            _verificationMessage = verificationMessage;
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

        protected override Task<SpoolerVerificationResult> VerifySpoolerLifecycleAsync(
            string printerName,
            string expectedDocument,
            CancellationToken cancellationToken)
        {
            var success = _verificationStage == PrintFailureStage.None;
            return Task.FromResult(new SpoolerVerificationResult(
                success,
                _verificationStage,
                _verificationMessage,
                SpoolerJobId: null));
        }

        protected override (bool HasError, int ErrorCode, string Description) CheckPrinterErrorState(
            string printerName,
            System.Management.ManagementObjectSearcher searcher)
        {
            return (false, 0, "No Error");
        }
    }
}
