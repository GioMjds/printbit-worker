using PrintBit.Infrastructure.IPC;

namespace PrintBit.Tests;

public class WorkerCommandParserTests
{
    [Fact]
    public void ParseLine_AcceptsLegacyCommand()
    {
        var command = WorkerCommandParser.ParseLine(
            "{\"type\":\"pause_job\",\"spoolerCorrelationKey\":\"spool-1\"}");

        Assert.NotNull(command);
        Assert.Equal("pause_job", command!.Type);
        Assert.Null(command.ProtocolVersion);
        Assert.Null(command.CommandId);
    }

    [Fact]
    public void ParseLine_AcceptsCompleteV2Command()
    {
        var command = WorkerCommandParser.ParseLine(
            "{\"type\":\"cancel_job\",\"spoolerCorrelationKey\":\"spool-1\",\"protocolVersion\":2,\"commandId\":\"command-001\"}");

        Assert.NotNull(command);
        Assert.Equal(2, command!.ProtocolVersion);
        Assert.Equal("command-001", command.CommandId);
    }

    [Theory]
    [InlineData("{\"type\":\"cancel_job\",\"spoolerCorrelationKey\":\"spool-1\",\"protocolVersion\":2}")]
    [InlineData("{\"type\":\"cancel_job\",\"spoolerCorrelationKey\":\"spool-1\",\"commandId\":\"command-001\"}")]
    [InlineData("{\"type\":\"cancel_job\",\"spoolerCorrelationKey\":\"spool-1\",\"protocolVersion\":3,\"commandId\":\"command-001\"}")]
    [InlineData("{\"type\":\"cancel_job\",\"spoolerCorrelationKey\":\"spool-1\",\"protocolVersion\":null}")]
    [InlineData("{\"type\":\"cancel_job\",\"spoolerCorrelationKey\":\"spool-1\",\"commandId\":null}")]
    public void ParseLine_RejectsPartialOrUnsupportedV2Envelope(string json)
    {
        Assert.Null(WorkerCommandParser.ParseLine(json));
    }
}
