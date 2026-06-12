namespace PrintBit.Infrastructure.Services.PrintService;

public class PrintJobResult
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public bool ProcessSucceeded { get; set; }

    public bool VerificationSucceeded { get; set; }

    public PrintFailureStage FailureStage { get; set; } = PrintFailureStage.None;

    public int? ExitCode { get; set; }

    public static PrintJobResult Failed(
        PrintFailureStage stage,
        string message,
        int? exitCode = null)
    {
        return new PrintJobResult
        {
            Success = false,
            ProcessSucceeded = stage is PrintFailureStage.SpoolerVerification
                                    or PrintFailureStage.HardwareError,
            VerificationSucceeded = false,
            FailureStage = stage,
            Message = message,
            ExitCode = exitCode
        };
    }
}
