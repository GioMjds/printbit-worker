using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Shared.Configurations;

namespace PrintBit.Infrastructure.IPC;

public sealed class WorkerEventPipeClient
{
    private const int ConnectTimeoutMilliseconds = 500;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILogger<WorkerEventPipeClient> _logger;

    private readonly IpcSettings _settings;

    public WorkerEventPipeClient(
        ILogger<WorkerEventPipeClient> logger,
        IOptions<IpcSettings> options)
    {
        _logger = logger;
        _settings = options.Value;
    }

    public async Task SendAsync(
        WorkerPrintEvent evt,
        CancellationToken cancellationToken = default)
    {
        if (evt is null)
        {
            throw new ArgumentNullException(nameof(evt));
        }

        await using var client = new NamedPipeClientStream(
            ".",
            _settings.WorkerReturnPipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);

        try
        {
            await client.ConnectAsync(ConnectTimeoutMilliseconds, cancellationToken);

            var payload = JsonSerializer.Serialize(evt, JsonOptions) + "\n";
            var bytes = Encoding.UTF8.GetBytes(payload);

            await client.WriteAsync(bytes, cancellationToken);
            await client.FlushAsync(cancellationToken);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(
                ex,
                "Worker return pipe connection timed out");
        }
        catch (IOException ex)
        {
            _logger.LogWarning(
                ex,
                "Worker return pipe connection failed");
        }
    }
}
