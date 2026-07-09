using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrintBit.Application.Events;
using PrintBit.Application.Handlers;
using PrintBit.Application.Services;
using PrintBit.Application.StateMachine;
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;
using PrintBit.Shared.Enums;

namespace PrintBit.Tests;

public class HardwareOrchestratorTests
{
    [Fact]
    public async Task PrintRequest_IsRejectedOutsideReadyToPrint()
    {
        var printService = new OrchestratorFakePrintService();
        var stateMachine = new TransactionStateMachine(NullLogger<TransactionStateMachine>.Instance);
        var pipeServer = new FakeNamedPipeServer();
        var sut = CreateSut(stateMachine, printService, pipeServer);

        var accepted = await sut.HandlePrintRequestAsync(
            new StartPrintEvent { FilePath = @"C:\PrintBit\sample.pdf" },
            "queue");

        Assert.False(accepted);
        Assert.Equal(0, printService.CallCount);
    }

    [Fact]
    public async Task PrintRequest_RunsWhenReadyToPrint()
    {
        var printService = new OrchestratorFakePrintService();
        var stateMachine = new TransactionStateMachine(NullLogger<TransactionStateMachine>.Instance);
        stateMachine.TryInsertCoin(5);
        var pipeServer = new FakeNamedPipeServer();
        var sut = CreateSut(stateMachine, printService, pipeServer);

        var accepted = await sut.HandlePrintRequestAsync(
            new StartPrintEvent { FilePath = @"C:\PrintBit\sample.pdf" },
            "queue");

        Assert.True(accepted);
        Assert.Equal(1, printService.CallCount);
        Assert.Equal(TransactionState.Success, stateMachine.CurrentState);
    }

    [Fact]
    public async Task ResetPipeCommand_ResetsFailedTransaction()
    {
        var printService = new OrchestratorFakePrintService();
        var stateMachine = new TransactionStateMachine(NullLogger<TransactionStateMachine>.Instance);
        stateMachine.TryInsertCoin(5);
        stateMachine.TryStartPrinting();
        stateMachine.TryMarkFailed("verification failed");
        var pipeServer = new FakeNamedPipeServer();
        var sut = CreateSut(stateMachine, printService, pipeServer);

        await sut.HandlePipeMessageAsync(
            new PipeMessage
            {
                Type = PipeMessageType.ResetTransactionRequest
            });

        Assert.Equal(TransactionState.Idle, stateMachine.CurrentState);
        Assert.Equal(0m, stateMachine.CurrentBalance);
    }

    private static HardwareOrchestrator CreateSut(
        TransactionStateMachine stateMachine,
        OrchestratorFakePrintService printService,
        INamedPipeServer pipeServer)
    {
        var startPrint = new StartPrintHandler(
            NullLogger<StartPrintHandler>.Instance,
            stateMachine,
            printService,
            Options.Create(new HardwareSettings()));

        var coinHandler = new CoinInsertedHandler(
            NullLogger<CoinInsertedHandler>.Instance,
            stateMachine);

        return new HardwareOrchestrator(
            NullLogger<HardwareOrchestrator>.Instance,
            coinHandler,
            startPrint,
            stateMachine,
            pipeServer);
    }
}

public class OrchestratorFakePrintService : IPrintService
{
    public int CallCount { get; private set; }

    public Task<PrintJobResult> PrintAsync(PrintJobRequest request, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(new PrintJobResult
        {
            Success = true,
            SumatraProcessSucceeded = true,
            VerificationSucceeded = true
        });
    }
}

public class FakeNamedPipeServer : INamedPipeServer
{
    public event Func<PipeMessage, CancellationToken, Task>? MessageReceived;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task BroadcastAsync(PipeMessage message, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
