namespace PrintBit.Infrastructure.Services.SerialService
{
    public interface ISerialConnection
    {
        bool IsConnected { get; }
        
        void Connect(string portName, int baudRate);

        void Disconnect();

        void Send(string data);

        event EventHandler<string>? DataReceived; 
    }
}
