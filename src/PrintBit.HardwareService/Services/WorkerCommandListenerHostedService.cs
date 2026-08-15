using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;

namespace PrintBit.HardwareService.Services;

public class WorkerCommandListenerHostedService : BackgroundService
{
    private readonly IJobOrchestrator _jobOrchestrator;
    private readonly ILogger<WorkerCommandListenerHostedService> _logger;
    private readonly string _pipeName;

    public WorkerCommandListenerHostedService(
        IJobOrchestrator jobOrchestrator,
        ILogger<WorkerCommandListenerHostedService> logger,
        IOptions<IpcSettings> ipcOptions)
    {
        _jobOrchestrator = jobOrchestrator;
        _logger = logger;
        _pipeName = ipcOptions.Value.WorkerCommandPipeName;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Command Pipe Listener on \\\\.\\pipe\\{PipeName}", _pipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipeStream = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipeStream.WaitForConnectionAsync(stoppingToken);
                _logger.LogInformation("Inbound client connected to command pipe.");

                using var reader = new StreamReader(pipeStream, Encoding.UTF8, leaveOpen: true);
                string? line;
                while ((line = await reader.ReadLineAsync(stoppingToken)) != null)
                {
                    var cmd = WorkerCommandParser.ParseLine(line);
                    if (cmd != null)
                    {
                        _logger.LogInformation("Received command {Type} for correlation key {Key}", cmd.Type, cmd.SpoolerCorrelationKey);
                        await HandleCommandAsync(cmd, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in command pipe listener loop. Retrying in 1s...");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private async Task HandleCommandAsync(WorkerCommandMessage cmd, CancellationToken cancellationToken)
    {
        switch (cmd.Type?.ToLowerInvariant())
        {
            case "cancel_job":
                await _jobOrchestrator.CancelActiveJobAsync(cmd.SpoolerCorrelationKey, cmd.Reason ?? "Cancelled");
                break;
            case "pause_job":
                _logger.LogInformation("Pause command received for key {Key}", cmd.SpoolerCorrelationKey);
                await _jobOrchestrator.PauseJobAsync(cmd.SpoolerCorrelationKey, cmd.Reason ?? "User paused");
                break;
            case "resume_job":
                _logger.LogInformation("Resume command received for key {Key}", cmd.SpoolerCorrelationKey);
                await _jobOrchestrator.ResumeJobAsync(cmd.SpoolerCorrelationKey);
                break;
            default:
                _logger.LogWarning("Unknown command type: {Type}", cmd.Type);
                break;
        }
    }
}
