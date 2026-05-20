namespace PrintBit.Infrastructure.Services.PrintService;

public enum PrintFailureStage
{
    None = 0,
    Validation,
    ProcessStart,
    ProcessExit,
    Timeout,
    SpoolerVerification,
    Unexpected
}
