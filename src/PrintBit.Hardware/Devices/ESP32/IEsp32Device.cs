namespace PrintBit.Hardware.Devices.ESP32
{
    public interface IEsp32Device
    {
        bool IsConnected { get; }
        void Connect(string portName, int baudRate);
        void Disconnect();
        void SendCommand(string command);
        event EventHandler<Esp32Message>? MessageReceived;
    }
}
