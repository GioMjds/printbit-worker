using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrintBit.Infrastructure.Services.PrintService;

namespace PrintBit.Tests;

public class PrinterRecoveryContractsTests
{
    [Fact]
    public async Task TryAcquireRecovery_ReturnsBusyWhilePrintLeaseIsHeld()
    {
        var coordinator = new PrintOperationCoordinator();
        using var printLease = await coordinator.AcquirePrintAsync(CancellationToken.None);

        var acquired = coordinator.TryAcquireRecovery(out var recoveryLease);

        Assert.False(acquired);
        Assert.Null(recoveryLease);
    }

    [Fact]
    public void TryAcquireRecovery_AllowsOnlyOneRecoveryLease()
    {
        var coordinator = new PrintOperationCoordinator();

        Assert.True(coordinator.TryAcquireRecovery(out var first));
        Assert.False(coordinator.TryAcquireRecovery(out var second));
        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Theory]
    [InlineData(PrinterRecoveryOutcome.Healthy, "\"healthy\"")]
    [InlineData(PrinterRecoveryOutcome.Recovered, "\"recovered\"")]
    [InlineData(PrinterRecoveryOutcome.ManualInterventionRequired, "\"manual_intervention_required\"")]
    [InlineData(PrinterRecoveryOutcome.WorkerBusy, "\"worker_busy\"")]
    [InlineData(PrinterRecoveryOutcome.RestartFailed, "\"restart_failed\"")]
    [InlineData(PrinterRecoveryOutcome.InvalidRequest, "\"invalid_request\"")]
    public void PrinterRecoveryOutcome_SerializesToSpecifiedWireValue(
        PrinterRecoveryOutcome outcome,
        string expectedJson)
    {
        var options = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        };

        var json = JsonSerializer.Serialize(outcome, options);

        Assert.Equal(expectedJson, json);
    }
}
