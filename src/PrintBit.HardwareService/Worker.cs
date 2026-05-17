using Microsoft.Extensions.Options;
using PrintBit.Application.Queues;
using PrintBit.Hardware.Devices.ESP32;
using PrintBit.Shared.Configurations;

namespace PrintBit.HardwareService;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;

    private readonly IEsp32Device _esp32;

    private readonly HardwareEventQueue _queue;

    private readonly HardwareSettings _settings;

    public Worker(
        ILogger<Worker> logger,
        IEsp32Device esp32,
        HardwareEventQueue queue,
        IOptions<HardwareSettings> options)
    {
        _logger = logger;

        _esp32 = esp32;

        _queue = queue;

        _settings = options.Value;

        _esp32.MessageReceived += OnMessageReceived;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _esp32.Connect(
            _settings.Esp32Port,
            _settings.Esp32BaudRate);

        _logger.LogInformation(
            "ESP32 worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(
                1000,
                stoppingToken);
        }
    }

    private async void OnMessageReceived(
        object? sender,
        Esp32Message message)
    {
        try
        {
            await _queue.EnqueueAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to enqueue hardware event");
        }
    }

    public override async Task StopAsync(
        CancellationToken cancellationToken)
    {
        _esp32.Disconnect();

        await base.StopAsync(cancellationToken);
    }
}