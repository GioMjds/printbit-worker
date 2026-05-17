namespace PrintBit.Infrastructure.Services.PrintService;

public interface IPrintRecoveryService
{
    Task RecoverAsync(CancellationToken cancellationToken = default);
}