using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PrintBit.Infrastructure.IPC;

public class NamedPipeServer : INamedPipeServer
{
    private readonly ILogger<NamedPipeServer> _logger;

    private readonly List<NamedPipeServerStream> _clients =
        [];

    private const string PipeName = "printbit.hardware";

    public NamedPipeServer(
        ILogger<NamedPipeServer> logger)
    {
        _logger = logger;
    }

    public async Task StartAsync(
        CancellationToken cancellationToken = default)
    {
        _ = AcceptClientsAsync(cancellationToken);

        await Task.CompletedTask;
    }

    public async Task BroadcastAsync(
        PipeMessage message,
        CancellationToken cancellationToken = default)
    {
        var json =
            JsonSerializer.Serialize(message);

        var bytes =
            Encoding.UTF8.GetBytes(json + "\n");

        var disconnectedClients =
            new List<NamedPipeServerStream>();

        foreach (var client in _clients)
        {
            try
            {
                if (!client.IsConnected)
                {
                    disconnectedClients.Add(client);

                    continue;
                }

                await client.WriteAsync(
                    bytes,
                    cancellationToken);

                await client.FlushAsync(
                    cancellationToken);
            }
            catch
            {
                disconnectedClients.Add(client);
            }
        }

        foreach (var disconnected in disconnectedClients)
        {
            disconnected.Dispose();

            _clients.Remove(disconnected);
        }
    }

    private async Task AcceptClientsAsync(
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var server =
                new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.Out,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

            await server.WaitForConnectionAsync(
                cancellationToken);

            _clients.Add(server);

            _logger.LogInformation(
                "Named pipe client connected");
        }
    }
}