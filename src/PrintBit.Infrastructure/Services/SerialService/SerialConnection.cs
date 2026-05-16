using System.IO.Ports;

namespace PrintBit.Infrastructure.Services.SerialService;

public class SerialConnection : ISerialConnection
{
    private SerialPort? _serialPort;

    public bool IsConnected =>
        _serialPort?.IsOpen ?? false;

    public event EventHandler<string>? DataReceived;

    public void Connect(string portName, int baudRate)
    {
        if (IsConnected)
            return;

        _serialPort = new SerialPort(portName, baudRate);

        _serialPort.DataReceived += OnDataReceived;

        _serialPort.Open();
    }

    public void Disconnect()
    {
        if (_serialPort is null)
            return;

        _serialPort.DataReceived -= OnDataReceived;

        if (_serialPort.IsOpen)
            _serialPort.Close();
    }

    public void Send(string data)
    {
        if (!IsConnected)
            return;

        _serialPort?.WriteLine(data);
    }

    private void OnDataReceived(
        object sender,
        SerialDataReceivedEventArgs e)
    {
        if (_serialPort is null)
            return;

        var data = _serialPort.ReadLine();

        DataReceived?.Invoke(this, data);
    }
}