namespace PrintBit.Infrastructure.Services.PrintService;

public class PrintJobSettings
{
    public int Copies { get; set; } = 1;
    public bool Color { get; set; } = false;
    public string? PageRange { get; set; }
    public string? Orientation { get; set; }
}
