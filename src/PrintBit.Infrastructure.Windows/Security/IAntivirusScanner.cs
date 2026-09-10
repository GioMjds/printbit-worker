namespace PrintBit.Infrastructure.Windows.Security;

public interface IAntivirusScanner
{
    Task<DefenderHealth> GetHealthAsync(CancellationToken cancellationToken);
    Task<DefenderScanResult> ScanFileAsync(string filePath, CancellationToken cancellationToken);
}

public sealed record DefenderHealth(string Status, double? SignatureAgeHours, string? Detail, string? ErrorCode);
public sealed record DefenderScanResult(string Status, string? DetectionName, string? Detail, string? ErrorCode);