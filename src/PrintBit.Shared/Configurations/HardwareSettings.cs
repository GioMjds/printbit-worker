namespace PrintBit.Shared.Configurations
{
    public class HardwareSettings
    {
        public string Esp32Port { get; set; } = "COM3";
        public int Esp32BaudRate { get; set; } = 115200;
        public int WatchdogIntervalSeconds { get; set; } = 5;
    }
}
