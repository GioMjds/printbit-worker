using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.PrintService;

namespace PrintBit.Tests;

internal sealed class FakeNamedPipeServer : INamedPipeServer
{
    public event Func<PipeMessage, CancellationToken, Task>? MessageReceived;

    public List<PipeMessage> Broadcasts { get; } = [];

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task BroadcastAsync(
        PipeMessage message,
        CancellationToken cancellationToken = default)
    {
        Broadcasts.Add(message);
        return Task.CompletedTask;
    }

    public Task EmitAsync(
        PipeMessage message,
        CancellationToken cancellationToken = default)
    {
        if (MessageReceived is null)
        {
            return Task.CompletedTask;
        }

        return MessageReceived.Invoke(message, cancellationToken);
    }
}

internal sealed class FakePrintService : IPrintService
{
    public int CallCount { get; private set; }

    public bool ThrowOnPrint { get; set; }

    public PrintJobResult Result { get; set; } = new()
    {
        Success = true,
        SumatraProcessSucceeded = true,
        VerificationSucceeded = true,
        Message = "ok"
    };

    public Task<PrintJobResult> PrintAsync(
        PrintJobRequest request,
        CancellationToken cancellationToken = default)
    {
        CallCount++;

        if (ThrowOnPrint)
        {
            throw new InvalidOperationException("print error");
        }

        return Task.FromResult(Result);
    }
}

internal sealed class FakeRecoveryService : IPrintRecoveryService
{
    public int RecoveryCallCount { get; private set; }

    public Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        RecoveryCallCount++;
        return Task.CompletedTask;
    }
}
