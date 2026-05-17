using Microsoft.Extensions.Logging;
using PrintBit.Infrastructure.Services.SerialService;

namespace PrintBit.Hardware.Devices.ESP32;

public class Esp32Device : IEsp32Device
{
    private readonly ILogger<Esp32Device> _logger;

    private readonly ISerialConnection _serialConnection;

    public bool IsConnected => _serialConnection.IsConnected;

    public event EventHandler<Esp32Message>? MessageReceived;

    public Esp32Device(
        ILogger<Esp32Device> logger,
        ISerialConnection serialConnection)
    {
        _logger = logger;
        _serialConnection = serialConnection;

        _serialConnection.DataReceived += OnDataReceived;
    }

    public void Connect(
        string portName,
        int baudRate)
    {
        _serialConnection.Connect(portName, baudRate);

        _logger.LogInformation(
            "ESP32 connected on {port}",
            portName);
    }

    public void Disconnect()
    {
        _serialConnection.Disconnect();

        _logger.LogWarning(
            "ESP32 disconnected");
    }

    public void SendCommand(string command)
    {
        _serialConnection.Send(command);

        _logger.LogInformation(
            "ESP32 command sent: {command}",
            command);
    }

    private void OnDataReceived(
        object? sender,
        string rawData)
    {
        _logger.LogInformation(
            "ESP32 raw data: {rawData}",
            rawData);

        var message = ParseMessage(rawData);

        MessageReceived?.Invoke(this, message);
    }

    private Esp32Message ParseMessage(string raw)
    {
        raw = raw.Trim();

        if (raw.StartsWith("COIN:"))
        {
            var valueText = raw.Replace("COIN:", "");

            int.TryParse(valueText, out var value);

            return new Esp32Message
            {
                Type = Esp32MessageType.CoinInserted,
                Value = value,
                Raw = raw
            };
        }

        if (raw == "HOPPER:DONE")
        {
            return new Esp32Message
            {
                Type = Esp32MessageType.HopperCompleted,
                Raw = raw
            };
        }

        if (raw == "PING")
        {
            return new Esp32Message
            {
                Type = Esp32MessageType.Heartbeat,
                Raw = raw
            };
        }

        return new Esp32Message
        {
            Type = Esp32MessageType.Unknown,
            Raw = raw
        };
    }
}