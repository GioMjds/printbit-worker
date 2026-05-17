namespace PrintBit.Infrastructure.Services.PrintService
{
    public class PrintJobRequest
    {
        public string FilePath { get; set; } = string.Empty;
        public string PrinterName { get; set; } = string.Empty;
        public int Copies { get; set; } = 1;
    }
}
