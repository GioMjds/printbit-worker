using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;

namespace PrintBit.Tests;

// Integration-style tests for the real PrintService class.
//
// The existing PrintServiceTests cover the *dispatch* behavior (given a stubbed
// process result and a stubbed verification result, the final PrintJobResult
// reflects the verification). Those tests use StubPrintService, which bypasses
// most of PrintService.PrintAsync's own logic (path validation, the PrintLock
// semaphore, the BeginAttempt lifecycle, the cancellation handling).
//
// These tests use a *real* PrintService subclass and override only the
// protected virtual seams (PathExists, GetSumatraExecutablePath,
// ExecutePrintProcessAsync, VerifySpoolerLifecycleAsync, CheckPrinterErrorState)
// to keep the test off the file system, the registry, and WMI. The seams are
// exactly the test points PrintService was designed to expose.
public class PrintServiceIntegrationTests
{
    [Fact]
    public async Task FilePathMissing_ReturnsValidationFailure_WithoutInvokingProcess()
    {
        var processCalls = 0;
        var sut = CreateService(
            fileExists: false,
            sumatraPathExists: true,
            onExecute: (_, _) =>
            {
                processCalls++;
                return Task.FromResult(SuccessProcessResult());
            });

        var result = await sut.PrintAsync(SampleRequest());

        Assert.Equal(PrintFailureStage.Validation, result.FailureStage);
        Assert.Equal("Print file does not exist", result.Message);
        Assert.False(result.Success);
        Assert.Equal(0, processCalls);
    }

    [Fact]
    public async Task SumatraMissing_ReturnsValidationFailure_WithoutInvokingProcess()
    {
        var processCalls = 0;
        var sut = CreateService(
            fileExists: true,
            sumatraPathExists: false,
            onExecute: (_, _) =>
            {
                processCalls++;
                return Task.FromResult(SuccessProcessResult());
            });

        var result = await sut.PrintAsync(SampleRequest());

        Assert.Equal(PrintFailureStage.Validation, result.FailureStage);
        Assert.Equal("SumatraPDF executable not found", result.Message);
        Assert.False(result.Success);
        Assert.Equal(0, processCalls);
    }

    [Fact]
    public async Task ProcessSucceeds_AndVerificationSucceeds_ReturnsFullySuccessfulResult()
    {
        var sut = CreateService(
            fileExists: true,
            sumatraPathExists: true,
            onExecute: (_, _) => Task.FromResult(SuccessProcessResult()),
            onVerify: (_, _, _) => Task.FromResult(
                new SpoolerVerificationResult(
                    true,
                    PrintFailureStage.None,
                    "Spooler lifecycle verified",
                    SpoolerJobId: "42")));

        var result = await sut.PrintAsync(SampleRequest());

        Assert.True(result.Success);
        Assert.True(result.SumatraProcessSucceeded);
        Assert.True(result.VerificationSucceeded);
        Assert.Equal(PrintFailureStage.None, result.FailureStage);
        Assert.Equal("42", result.SpoolerJobId);
    }

    [Fact]
    public async Task ProcessSucceeds_ButVerificationFails_PropagatesSumatraProcessSucceededFlag()
    {
        // This is the case the SumatraProcessSucceeded rename was about:
        // the Sumatra process exited cleanly but the spooler never saw a
        // matching job. The result must be a failure with the verification
        // stage, but the SumatraProcessSucceeded flag must still be true so
        // the state machine can advance to Verifying.
        var sut = CreateService(
            fileExists: true,
            sumatraPathExists: true,
            onExecute: (_, _) => Task.FromResult(SuccessProcessResult()),
            onVerify: (_, _, _) => Task.FromResult(
                new SpoolerVerificationResult(
                    false,
                    PrintFailureStage.SpoolerVerification,
                    "Spooler job did not clear before timeout",
                    SpoolerJobId: null)));

        var result = await sut.PrintAsync(SampleRequest());

        Assert.False(result.Success);
        Assert.Equal(PrintFailureStage.SpoolerVerification, result.FailureStage);
        Assert.True(result.SumatraProcessSucceeded,
            "SumatraProcessSucceeded must stay true when only verification failed");
        Assert.Contains("timeout", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessFails_SkipsVerificationAndReturnsProcessFailure()
    {
        var verifyCalls = 0;
        var sut = CreateService(
            fileExists: true,
            sumatraPathExists: true,
            onExecute: (_, _) => Task.FromResult(
                new PrintJobResult
                {
                    Success = false,
                    Message = "Sumatra exited with code 1",
                    ExitCode = 1,
                    FailureStage = PrintFailureStage.ProcessExit
                }),
            onVerify: (_, _, _) =>
            {
                verifyCalls++;
                return Task.FromResult(
                    new SpoolerVerificationResult(
                        true, PrintFailureStage.None, "should not run", SpoolerJobId: null));
            });

        var result = await sut.PrintAsync(SampleRequest());

        Assert.False(result.Success);
        Assert.Equal(PrintFailureStage.ProcessExit, result.FailureStage);
        Assert.Equal(0, verifyCalls);
    }

    [Fact]
    public async Task Cancellation_PropagatesAsOperationCanceledException_NotAsFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sut = CreateService(
            fileExists: true,
            sumatraPathExists: true,
            onExecute: (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(SuccessProcessResult());
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.PrintAsync(SampleRequest(), cts.Token));
    }

    [Fact]
    public async Task PrintLock_SerializesConcurrentCalls()
    {
        // Two concurrent calls must not overlap. If they did, the second
        // would short-circuit because the file is still in use — but more
        // importantly, the semaphore guarantees serialized SumatraPDF
        // invocations, which is the actual invariant.
        var insideExecute = 0;
        var maxConcurrent = 0;
        var concurrentNow = 0;

        var sut = CreateService(
            fileExists: true,
            sumatraPathExists: true,
            onExecute: async (_, _) =>
            {
                var observed = Interlocked.Increment(ref insideExecute);
                InterlockedMax(ref concurrentNow, ref maxConcurrent, observed);
                await Task.Delay(150);
                Interlocked.Decrement(ref insideExecute);
                return SuccessProcessResult();
            },
            onVerify: (_, _, _) => Task.FromResult(
                new SpoolerVerificationResult(
                    true, PrintFailureStage.None, "ok", SpoolerJobId: "1")));

        var task1 = Task.Run(() => sut.PrintAsync(SampleRequest()));
        var task2 = Task.Run(() => sut.PrintAsync(SampleRequest()));

        var results = await Task.WhenAll(task1, task2);

        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal(1, maxConcurrent);
    }

    private static PrintJobRequest SampleRequest() => new()
    {
        FilePath = @"C:\PrintBit\sample.pdf",
        PrinterName = "EPSON L5290 Series"
    };

    private static PrintJobResult SuccessProcessResult() => new()
    {
        Success = true,
        Message = "Print process exited successfully",
        SumatraProcessSucceeded = true,
        VerificationSucceeded = false,
        FailureStage = PrintFailureStage.None,
        ExitCode = 0
    };

    private static void InterlockedMax(ref int location, ref int max, int value)
    {
        var snapshot = Volatile.Read(ref max);
        while (snapshot < value
            && Interlocked.CompareExchange(ref max, value, snapshot) != snapshot)
        {
            snapshot = Volatile.Read(ref max);
        }
    }

    private static IntegrationStub CreateService(
        bool fileExists,
        bool sumatraPathExists,
        Func<PrintJobRequest, CancellationToken, Task<PrintJobResult>> onExecute,
        Func<string, string, CancellationToken, Task<SpoolerVerificationResult>>? onVerify = null)
    {
        return new IntegrationStub(
            fileExists: fileExists,
            sumatraPathExists: sumatraPathExists,
            onExecute: onExecute,
            onVerify: onVerify ?? ((_, _, _) => Task.FromResult(
                new SpoolerVerificationResult(
                    true, PrintFailureStage.None, "ok", SpoolerJobId: "1"))));
    }

    // Real PrintService subclass. Overrides only the protected virtual seams
    // so the tests run without touching the file system, registry, or WMI.
    private sealed class IntegrationStub : PrintService
    {
        private readonly bool _fileExists;
        private readonly bool _sumatraPathExists;
        private readonly Func<PrintJobRequest, CancellationToken, Task<PrintJobResult>> _onExecute;
        private readonly Func<string, string, CancellationToken, Task<SpoolerVerificationResult>> _onVerify;

        public IntegrationStub(
            bool fileExists,
            bool sumatraPathExists,
            Func<PrintJobRequest, CancellationToken, Task<PrintJobResult>> onExecute,
            Func<string, string, CancellationToken, Task<SpoolerVerificationResult>> onVerify)
            : base(
                NullLogger<PrintService>.Instance,
                Options.Create(new HardwareSettings()),
                new FakeRecoveryService(),
                new PrintHealthCoordinator())
        {
            _fileExists = fileExists;
            _sumatraPathExists = sumatraPathExists;
            _onExecute = onExecute;
            _onVerify = onVerify;
        }

        protected override bool PathExists(string path)
        {
            // First call is the request file; second is the Sumatra path.
            if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return _fileExists;
            }
            return _sumatraPathExists;
        }

        protected override string GetSumatraExecutablePath()
        {
            return @"C:\fake\SumatraPDF.exe";
        }

        protected override Task<PrintJobResult> ExecutePrintProcessAsync(
            PrintJobRequest request,
            string sumatraPath,
            CancellationToken cancellationToken)
        {
            return _onExecute(request, cancellationToken);
        }

        protected override Task<SpoolerVerificationResult> VerifySpoolerLifecycleAsync(
            string printerName,
            string expectedDocument,
            CancellationToken cancellationToken)
        {
            return _onVerify(printerName, expectedDocument, cancellationToken);
        }

        protected override (bool HasError, int ErrorCode, string Description) CheckPrinterErrorState(
            string printerName,
            System.Management.ManagementObjectSearcher searcher)
        {
            return (false, 0, "No Error");
        }
    }
}
