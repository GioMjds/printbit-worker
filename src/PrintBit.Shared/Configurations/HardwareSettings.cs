namespace PrintBit.Shared.Configurations;

public class HardwareSettings
{
    public string Esp32Port { get; set; } = "COM3";
    public int Esp32BaudRate { get; set; } = 115200;
    public int PrintTimeoutSeconds { get; set; } = 120;
    public string PrinterName { get; set; } = "EPSON L5290 Series";
    public string PrintQueueDirectory { get; set; } = "queue";
    public string? FailedDirectory { get; set; }
    public string SumatraPath { get; set; } = @"C:\Users\printbit\bin\SumatraPDF.exe";
    public string QpdfPath { get; set; } = @"C:\Users\printbit\bin\qpdf.exe";
    public int PdfSplitTimeoutSeconds { get; set; } = 30;
    public int PauseTimeoutMinutes { get; set; } = 15;
    public int PostClearGuardDelaySeconds { get; set; } = 12;
}
