using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;

namespace PrintBit.Infrastructure.Windows.PrinterMonitoring;

[SupportedOSPlatform("windows")]
public class PrinterRecoveryService : IPrinterRecoveryService
{
    private const string DefaultPrinterName = "EPSON L5290 Series";
    private const string RestartSpoolerAction = "RestartSpooler";

    private readonly IPrinterHealthMonitor _healthMonitor;
    private readonly IPrintSpoolerController _spoolerController;
    private readonly IPrinterOperationCoordinator _coordinator;
    private readonly PrinterRecoverySettings _recoverySettings;
    private readonly HardwareSettings? _hardwareSettings;
    private readonly ILogger<PrinterRecoveryService>? _logger;

    public PrinterRecoveryService(
        IPrinterHealthMonitor healthMonitor,
        IPrintSpoolerController spoolerController,
        IPrinterOperationCoordinator coordinator,
        IOptions<PrinterRecoverySettings> recoverySettings,
        IOptions<HardwareSettings>? hardwareSettings = null,
        ILogger<PrinterRecoveryService>? logger = null)
    {
        _healthMonitor = healthMonitor ?? throw new ArgumentNullException(nameof(healthMonitor));
        _spoolerController = spoolerController ?? throw new ArgumentNullException(nameof(spoolerController));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _recoverySettings = recoverySettings?.Value ?? new PrinterRecoverySettings();
        _hardwareSettings = hardwareSettings?.Value;
        _logger = logger;
    }

    public async Task<PrinterRecoveryResult> GetStatusAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var printerName = ResolvePrinterName();

        var spoolerStatus = await _spoolerController.GetStatusAsync(cancellationToken);
        var diagnostic = _healthMonitor.GetDiagnostic(printerName);

        PrinterRecoveryOutcome outcome;
        string message;

        if (spoolerStatus.IsRunning && diagnostic.IsHealthy)
        {
            outcome = PrinterRecoveryOutcome.Healthy;
            message = "Printer and print spooler are healthy.";
        }
        else if (diagnostic.IssueKind == PrinterHealthIssueKind.PhysicalFault)
        {
            outcome = PrinterRecoveryOutcome.ManualInterventionRequired;
            message = !string.IsNullOrWhiteSpace(diagnostic.WinSpoolDescription)
                ? diagnostic.WinSpoolDescription
                : (!string.IsNullOrWhiteSpace(diagnostic.WmiDescription)
                    ? diagnostic.WmiDescription
                    : "Physical printer fault detected. Manual intervention required.");
        }
        else if (!spoolerStatus.IsRunning)
        {
            outcome = PrinterRecoveryOutcome.ManualInterventionRequired;
            message = "Print Spooler service is not running.";
        }
        else
        {
            outcome = PrinterRecoveryOutcome.ManualInterventionRequired;
            message = !string.IsNullOrWhiteSpace(diagnostic.WinSpoolDescription)
                ? diagnostic.WinSpoolDescription
                : (!string.IsNullOrWhiteSpace(diagnostic.WmiDescription)
                    ? diagnostic.WmiDescription
                    : "Printer fault or offline state detected.");
        }

        return new PrinterRecoveryResult
        {
            RequestId = string.Empty,
            Type = PrinterRecoveryCommandType.GetPrinterRecoveryStatus,
            Outcome = outcome,
            Action = null,
            SpoolerState = spoolerStatus.Status,
            PrinterState = diagnostic.PrinterState.ToString(),
            IssueKind = diagnostic.IssueKind.ToString(),
            Message = message,
            StartedAt = startedAt,
            CompletedAt = DateTime.UtcNow
        };
    }

    public async Task<PrinterRecoveryResult> AttemptRepairAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;

        if (!_coordinator.TryAcquireRecovery(out var lease) || lease == null)
        {
            _logger?.LogWarning("AttemptRepairAsync rejected: operation lease could not be acquired (worker busy).");
            return new PrinterRecoveryResult
            {
                RequestId = string.Empty,
                Type = PrinterRecoveryCommandType.AttemptPrinterRecovery,
                Outcome = PrinterRecoveryOutcome.WorkerBusy,
                Action = null,
                SpoolerState = null,
                PrinterState = null,
                IssueKind = null,
                Message = "Printer recovery is unavailable while an operation is active.",
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow
            };
        }

        using (lease)
        {
            var printerName = ResolvePrinterName();

            var spoolerStatus = await _spoolerController.GetStatusAsync(cancellationToken);
            var diagnostic = _healthMonitor.GetDiagnostic(printerName);

            // 1. If healthy and spooler running: no action needed
            if (spoolerStatus.IsRunning && diagnostic.IsHealthy)
            {
                _logger?.LogInformation("AttemptRepairAsync: Printer '{PrinterName}' is healthy. No repair needed.", printerName);
                return new PrinterRecoveryResult
                {
                    RequestId = string.Empty,
                    Type = PrinterRecoveryCommandType.AttemptPrinterRecovery,
                    Outcome = PrinterRecoveryOutcome.Healthy,
                    Action = null,
                    SpoolerState = spoolerStatus.Status,
                    PrinterState = diagnostic.PrinterState.ToString(),
                    IssueKind = diagnostic.IssueKind.ToString(),
                    Message = "Printer is healthy. No recovery action needed.",
                    StartedAt = startedAt,
                    CompletedAt = DateTime.UtcNow
                };
            }

            // 2. Physical fault: requires manual intervention, do not restart spooler
            if (diagnostic.IssueKind == PrinterHealthIssueKind.PhysicalFault)
            {
                var physicalMsg = !string.IsNullOrWhiteSpace(diagnostic.WinSpoolDescription)
                    ? diagnostic.WinSpoolDescription
                    : (!string.IsNullOrWhiteSpace(diagnostic.WmiDescription)
                        ? diagnostic.WmiDescription
                        : "Physical printer fault detected. Manual intervention required.");

                _logger?.LogWarning(
                    "AttemptRepairAsync: Printer '{PrinterName}' has physical fault ({Message}). Spooler restart bypassed.",
                    printerName,
                    physicalMsg);

                return new PrinterRecoveryResult
                {
                    RequestId = string.Empty,
                    Type = PrinterRecoveryCommandType.AttemptPrinterRecovery,
                    Outcome = PrinterRecoveryOutcome.ManualInterventionRequired,
                    Action = null,
                    SpoolerState = spoolerStatus.Status,
                    PrinterState = diagnostic.PrinterState.ToString(),
                    IssueKind = diagnostic.IssueKind.ToString(),
                    Message = physicalMsg,
                    StartedAt = startedAt,
                    CompletedAt = DateTime.UtcNow
                };
            }

            // 3. Windows-side / unknown fault or spooler stopped: perform one Spooler restart
            _logger?.LogInformation(
                "AttemptRepairAsync: Printer '{PrinterName}' has recoverable fault ({State}, {IssueKind}). Attempting Spooler restart.",
                printerName,
                diagnostic.PrinterState,
                diagnostic.IssueKind);

            var restartResult = await _spoolerController.RestartAsync(cancellationToken);
            if (!restartResult.Success)
            {
                var errorMsg = $"Print Spooler restart failed: {restartResult.Error}";
                _logger?.LogError("AttemptRepairAsync: {Message}", errorMsg);

                return new PrinterRecoveryResult
                {
                    RequestId = string.Empty,
                    Type = PrinterRecoveryCommandType.AttemptPrinterRecovery,
                    Outcome = PrinterRecoveryOutcome.RestartFailed,
                    Action = RestartSpoolerAction,
                    SpoolerState = restartResult.FinalStatus,
                    PrinterState = diagnostic.PrinterState.ToString(),
                    IssueKind = diagnostic.IssueKind.ToString(),
                    Message = errorMsg,
                    StartedAt = startedAt,
                    CompletedAt = DateTime.UtcNow
                };
            }

            // 4. Poll read-only diagnostic until healthy or timeout
            var recheckTimeoutSeconds = Math.Max(0, _recoverySettings.HealthRecheckTimeoutSeconds);
            var intervalSeconds = Math.Max(0, _recoverySettings.HealthRecheckIntervalSeconds);
            var deadline = DateTime.UtcNow.AddSeconds(recheckTimeoutSeconds);

            var latestDiagnostic = _healthMonitor.GetDiagnostic(printerName);

            while (!latestDiagnostic.IsHealthy && DateTime.UtcNow < deadline)
            {
                var delayMs = (int)(intervalSeconds * 1000);
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, cancellationToken);
                }

                latestDiagnostic = _healthMonitor.GetDiagnostic(printerName);

                if (delayMs == 0)
                {
                    break;
                }
            }

            if (latestDiagnostic.IsHealthy)
            {
                _logger?.LogInformation(
                    "AttemptRepairAsync: Printer '{PrinterName}' restored to healthy after Spooler restart.",
                    printerName);

                return new PrinterRecoveryResult
                {
                    RequestId = string.Empty,
                    Type = PrinterRecoveryCommandType.AttemptPrinterRecovery,
                    Outcome = PrinterRecoveryOutcome.Recovered,
                    Action = RestartSpoolerAction,
                    SpoolerState = restartResult.FinalStatus,
                    PrinterState = latestDiagnostic.PrinterState.ToString(),
                    IssueKind = latestDiagnostic.IssueKind.ToString(),
                    Message = "Print Spooler restarted successfully and printer health restored.",
                    StartedAt = startedAt,
                    CompletedAt = DateTime.UtcNow
                };
            }
            else
            {
                var failureMsg = $"Print Spooler restarted, but printer remained unhealthy ({latestDiagnostic.PrinterState}, {latestDiagnostic.IssueKind}). Manual intervention required.";
                _logger?.LogWarning("AttemptRepairAsync: {Message}", failureMsg);

                return new PrinterRecoveryResult
                {
                    RequestId = string.Empty,
                    Type = PrinterRecoveryCommandType.AttemptPrinterRecovery,
                    Outcome = PrinterRecoveryOutcome.ManualInterventionRequired,
                    Action = RestartSpoolerAction,
                    SpoolerState = restartResult.FinalStatus,
                    PrinterState = latestDiagnostic.PrinterState.ToString(),
                    IssueKind = latestDiagnostic.IssueKind.ToString(),
                    Message = failureMsg,
                    StartedAt = startedAt,
                    CompletedAt = DateTime.UtcNow
                };
            }
        }
    }

    private string ResolvePrinterName()
    {
        if (!string.IsNullOrWhiteSpace(_recoverySettings.PrinterName))
        {
            return _recoverySettings.PrinterName;
        }

        if (!string.IsNullOrWhiteSpace(_hardwareSettings?.PrinterName))
        {
            return _hardwareSettings.PrinterName;
        }

        return DefaultPrinterName;
    }
}
