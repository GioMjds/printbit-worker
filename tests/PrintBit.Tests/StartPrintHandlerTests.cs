using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrintBit.Application.Events;
using PrintBit.Application.Handlers;
using PrintBit.Application.StateMachine;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;
using PrintBit.Shared.Enums;

namespace PrintBit.Tests;

public class StartPrintHandlerTests
{
    [Fact]
    public async Task VerifiedPrintResult_MarksSuccess()
    {
        var stateMachine = CreateReadyStateMachine();
        var printService = new FakePrintService
        {
            Result = new PrintJobResult
            {
                Success = true,
                ProcessSucceeded = true,
                VerificationSucceeded = true,
                Message = "verified"
            }
        };

        var sut = CreateHandler(stateMachine, printService);

        await sut.HandleAsync(new StartPrintEvent { FilePath = @"C:\PrintBit\sample.pdf" });

        Assert.Equal(TransactionState.Success, stateMachine.CurrentState);
    }

    [Fact]
    public async Task FailedPrintResult_MarksFailed()
    {
        var stateMachine = CreateReadyStateMachine();
        var printService = new FakePrintService
        {
            Result = PrintJobResult.Failed(
                PrintFailureStage.SpoolerVerification,
                "no spooler job observed")
        };

        var sut = CreateHandler(stateMachine, printService);

        await sut.HandleAsync(new StartPrintEvent { FilePath = @"C:\PrintBit\sample.pdf" });

        Assert.Equal(TransactionState.Failed, stateMachine.CurrentState);
        Assert.Contains("SpoolerVerification", stateMachine.LastFailureReason);
    }

    [Fact]
    public async Task Exception_MarksFailed()
    {
        var stateMachine = CreateReadyStateMachine();
        var printService = new FakePrintService
        {
            ThrowOnPrint = true
        };

        var sut = CreateHandler(stateMachine, printService);

        await sut.HandleAsync(new StartPrintEvent { FilePath = @"C:\PrintBit\sample.pdf" });

        Assert.Equal(TransactionState.Failed, stateMachine.CurrentState);
        Assert.Contains("Unhandled print exception", stateMachine.LastFailureReason);
    }

    private static StartPrintHandler CreateHandler(
        TransactionStateMachine stateMachine,
        IPrintService printService)
    {
        return new StartPrintHandler(
            NullLogger<StartPrintHandler>.Instance,
            stateMachine,
            printService,
            Options.Create(new HardwareSettings()));
    }

    private static TransactionStateMachine CreateReadyStateMachine()
    {
        var stateMachine = new TransactionStateMachine(
            NullLogger<TransactionStateMachine>.Instance);

        stateMachine.TryInsertCoin(5);

        return stateMachine;
    }
}
