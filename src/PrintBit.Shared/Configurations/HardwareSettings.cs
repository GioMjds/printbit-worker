namespace PrintBit.Shared.Configurations;

public class HardwareSettings
{
    public string Esp32Port { get; set; } = "COM3";

    public int Esp32BaudRate { get; set; } = 115200;

    public int PrintTimeoutSeconds { get; set; } = 120;

    public string PrinterName { get; set; } = "EPSON L5290 Series";

    // Default queue path is a relative "queue" directory. The watcher resolves
    // it via Path.GetFullPath and creates it on startup if missing, so the same
    // value works in dev and in production. Override in appsettings.json or
    // appsettings.Development.json for environment-specific paths.
    public string PrintQueueDirectory { get; set; } = "queue";

    public string SumatraPath { get; set; } = @"C:\Users\printbit\bin\SumatraPDF.exe";
}
