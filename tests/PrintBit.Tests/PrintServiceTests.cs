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
                new FakeRecoveryService())
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

        protected override Task<(bool Success, string Message)> VerifySpoolerLifecycleAsync(
            string printerName,
            string expectedDocument,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_verificationResult);
        }
    }
}
