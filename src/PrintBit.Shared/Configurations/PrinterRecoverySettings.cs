namespace PrintBit.Shared.Configurations;

public class PrinterRecoverySettings
{
    public int SpoolerTransitionTimeoutSeconds { get; set; } = 30;

    public int HealthRecheckTimeoutSeconds { get; set; } = 10;

    public int HealthRecheckIntervalSeconds { get; set; } = 2;

    public string ServiceName { get; set; } = "Spooler";

    public string? PrinterName { get; set; }
}
