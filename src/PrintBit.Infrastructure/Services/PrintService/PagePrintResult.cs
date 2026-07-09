using PrintBit.Shared.Printing;

namespace PrintBit.Infrastructure.Services.PrintService;

public class PagePrintResult
{
    public PagePrintState State { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SpoolerJobId { get; set; }
    public PrintFailureStage FailureStage { get; set; }
}
