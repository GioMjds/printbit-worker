using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text;

namespace PrintBit.Infrastructure.Services.SerialService;

public class SerialConnection : ISerialConnection, IDisposable
{
    private readonly Func<string, int, ISerialPortAdapter> _portFactory;
    private readonly StringBuilder _buffer = new();
    private readonly object _bufferLock = new();
    private readonly object _connectionLock = new();

    private ISerialPortAdapter? _serialPort;
    private string? _currentPortName;

    public SerialConnection()
        : this((port, baud) => new DefaultSerialPortAdapter(port, baud))
    {
    }

    internal SerialConnection(Func<string, int, ISerialPortAdapter> portFactory)
    {
        _portFactory = portFactory;
    }

    public bool IsConnected => _serialPort?.IsOpen ?? false;

    public string? CurrentPortName => _currentPortName;

    public event EventHandler<string>? LineReceived;

    public event EventHandler<(bool isConnected, string? port, string? error)>? ConnectionChanged;

    [Obsolete("Use LineReceived instead.")]
    public event EventHandler<string>? DataReceived
    {
        add => LineReceived += value;
        remove => LineReceived -= value;
    }

    public void Connect(string portName, int baudRate)
    {
        lock (_connectionLock)
        {
            if (IsConnected)
                return;

            try
            {
                DisconnectInternal(suppressEvent: true);

                _serialPort = _portFactory(portName, baudRate);
                _serialPort.DataReceived += OnDataReceived;
                _serialPort.ErrorReceived += OnErrorReceived;
                _serialPort.Open();

                _currentPortName = portName;
                ConnectionChanged?.Invoke(this, (true, portName, null));
            }
            catch (Exception ex)
            {
                var failedPort = portName;
                DisconnectInternal(suppressEvent: true);
                ConnectionChanged?.Invoke(this, (false, failedPort, ex.Message));
                throw;
            }
        }
    }

    public void Disconnect()
    {
        lock (_connectionLock)
        {
            DisconnectInternal(suppressEvent: false);
        }
    }

    private void DisconnectInternal(bool suppressEvent)
    {
        var port = _currentPortName;
        var wasConnected = IsConnected;

        if (_serialPort != null)
        {
            try { _serialPort.DataReceived -= OnDataReceived; } catch { }
            try { _serialPort.ErrorReceived -= OnErrorReceived; } catch { }
            try
            {
                if (_serialPort.IsOpen)
                {
                    _serialPort.Close();
                }
            }
            catch { }
            try { _serialPort.Dispose(); } catch { }
            _serialPort = null;
        }

        lock (_bufferLock)
        {
            _buffer.Clear();
        }

        _currentPortName = null;

        if (!suppressEvent && (wasConnected || port != null))
        {
            ConnectionChanged?.Invoke(this, (false, port, null));
        }
    }

    public void SendLine(string data)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));

        lock (_connectionLock)
        {
            if (!IsConnected || _serialPort is null)
            {
                throw new InvalidOperationException("Serial port is not connected.");
            }

            var toSend = data.EndsWith('\n') ? data : data + "\n";
            _serialPort.Write(toSend);
        }
    }

    [Obsolete("Use SendLine instead.")]
    public void Send(string data) => SendLine(data);

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_serialPort is null || !_serialPort.IsOpen)
            return;

        try
        {
            var data = _serialPort.ReadExisting();
            ProcessIncomingData(data);
        }
        catch (Exception)
        {
            // Ignore unexpected read errors on abrupt port disconnects
        }
    }

    private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
    }

    internal void ProcessIncomingData(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            return;

        List<string> linesToEmit = new();

        lock (_bufferLock)
        {
            _buffer.Append(chunk);
            var content = _buffer.ToString();

            int newlineIndex;
            while ((newlineIndex = content.IndexOf('\n')) >= 0)
            {
                var line = content.Substring(0, newlineIndex);
                line = line.TrimEnd('\r');
                linesToEmit.Add(line);

                content = content.Substring(newlineIndex + 1);
            }

            _buffer.Clear();
            _buffer.Append(content);
        }

        foreach (var line in linesToEmit)
        {
            LineReceived?.Invoke(this, line);
        }
    }

    public void Dispose()
    {
        Disconnect();
        GC.SuppressFinalize(this);
    }
}