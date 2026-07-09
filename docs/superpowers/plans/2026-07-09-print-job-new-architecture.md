# Page-Level Print Dispatch and Patience Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the current print pipeline with a page-by-page dispatch model using qpdf split-pages, implementing a pause/resume/cancel patience mode driven by Epson physical buttons and health monitoring.

**Architecture:** A new `JobOrchestrator` splits the PDF into single pages, creates a print manifest, and runs a page dispatch loop. `PagePrinter` prints each single page, and `PrinterHealthMonitor` tracks hardware state, pauses on errors to wait for physical button interaction (Start/Stop), and resumes or cancels pages accordingly.

**Tech Stack:** C#, .NET 10, qpdf, SumatraPDF, WinSpool API, WMI, Powershell

---

## File Structure & Dependencies

To ensure we follow the dependency direction (`HardwareService` -> `Application` -> `Hardware` / `Infrastructure` -> `Shared`), the new and modified files are mapped as follows:

- **PrintBit.Shared** (shared models, config, and enums):
  - Modify: `src/PrintBit.Shared/Configurations/HardwareSettings.cs` — Adds `QpdfPath`, `PdfSplitTimeoutSeconds`, and `PauseTimeoutMinutes`.
  - Modify: `src/PrintBit.Shared/IPC/WorkerPrintEventType.cs` (or create if not present) — Adds new event enum values: `JobPaused`, `JobResumed`, `JobCompleted`.
  - Modify: `src/PrintBit.Shared/IPC/WorkerPrintEvent.cs` — Adds event models including `WorkerPrintPageResult` for progress/completed page listings.
  - Create: `src/PrintBit.Shared/Printing/PagePrintState.cs` — Core lifecycle enum for page states.
  - Create: `src/PrintBit.Shared/Printing/PagePrintEntry.cs` — Track metadata per page.

- **PrintBit.Infrastructure** (abstractions and core print/hardware operations):
  - Create: `src/PrintBit.Infrastructure/Services/PrintService/IPrinterHealthMonitor.cs` — Health monitoring & recovery abstraction.
  - Create: `src/PrintBit.Infrastructure/Services/PrintService/PagePrintResult.cs` — Result structure of single-page prints.
  - Create: `src/PrintBit.Infrastructure/Services/PrintService/IPagePrinter.cs` — Abstraction for printing a single page.
  - Create: `src/PrintBit.Infrastructure/Services/PrintService/PagePrinter.cs` — Spooler-aware single-page printer.

- **PrintBit.Infrastructure.Windows** (Windows-specific P/Invokes, WMI, processes):
  - Create: `src/PrintBit.Infrastructure.Windows/PrinterMonitoring/PrinterHealthMonitor.cs` — Integrates WMI/WinSpool API polling and Epson Status Monitor window checking. Replaces `PrinterMonitorService`, `PrintHealthCoordinator`, and `PrintRecoveryService`.

- **PrintBit.Application** (orchestration logic):
  - Create: `src/PrintBit.Application/Printing/IJobOrchestrator.cs` — Orchestrator abstraction.
  - Create: `src/PrintBit.Application/Printing/JobOrchestrator.cs` — Executes qpdf splitting, manifest generation, the main page loop, pre-flight pause, event signaling, and file cleanups.

- **PrintBit.HardwareService** (background worker entry point):
  - Create: `src/PrintBit.HardwareService/Services/PrintQueueWatcher.cs` — Background file watcher that delegates jobs to `IJobOrchestrator`. Replaces `PrintQueueWatcherService`.
  - Modify: `src/PrintBit.HardwareService/Program.cs` — Updates registrations.

---

## Tasks

### Task 1: Update Configuration and Shared Models

**Files:**
- Modify: `src/PrintBit.Shared/Configurations/HardwareSettings.cs`
- Modify: `src/PrintBit.Infrastructure/IPC/WorkerPrintEventType.cs`
- Modify: `src/PrintBit.Infrastructure/IPC/WorkerPrintEvent.cs`
- Create: `src/PrintBit.Shared/Printing/PagePrintState.cs`
- Create: `src/PrintBit.Shared/Printing/PagePrintEntry.cs`
- Test: `tests/PrintBit.Tests/WorkerPrintEventTests.cs`

- [ ] **Step 1: Write the failing test for configuration and shared models**
Add this test to `tests/PrintBit.Tests/WorkerPrintEventTests.cs`:
```csharp
using Xunit;
using PrintBit.Shared.Configurations;
using PrintBit.Infrastructure.IPC;
using PrintBit.Shared.Printing;
using System.Text.Json;

namespace PrintBit.Tests;

public class WorkerPrintEventTests
{
    [Fact]
    public void HardwareSettings_HasNewConfigFields()
    {
        var settings = new HardwareSettings
        {
            QpdfPath = @"C:\bin\qpdf.exe",
            PdfSplitTimeoutSeconds = 45,
            PauseTimeoutMinutes = 10
        };

        Assert.Equal(@"C:\bin\qpdf.exe", settings.QpdfPath);
        Assert.Equal(45, settings.PdfSplitTimeoutSeconds);
        Assert.Equal(10, settings.PauseTimeoutMinutes);
    }

    [Fact]
    public void WorkerPrintEvent_SerializesToCamelCase()
    {
        var evt = new WorkerPrintEvent
        {
            Type = WorkerPrintEventType.JobCompleted,
            TransactionId = "TX-1",
            SpoolerCorrelationKey = "SCK-1",
            Outcome = "partially_completed",
            TotalCopies = 2,
            TotalExpected = 4,
            CancelledCount = 1,
            CompletedCount = 3,
            Pages = new System.Collections.Generic.List<WorkerPrintPageResult>
            {
                new() { Page = 1, Copy = 1, State = "completed" },
                new() { Page = 2, Copy = 1, State = "cancelled" }
            }
        };

        var json = JsonSerializer.Serialize(evt, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        });

        Assert.Contains("\"type\":\"JobCompleted\"", json);
        Assert.Contains("\"transactionId\":\"TX-1\"", json);
        Assert.Contains("\"outcome\":\"partially_completed\"", json);
        Assert.Contains("\"pages\":[", json);
        Assert.Contains("\"state\":\"completed\"", json);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**
Run: `dotnet test --filter "FullyQualifiedName~WorkerPrintEventTests"`
Expected: FAIL with compilation errors (missing fields and new classes).

- [ ] **Step 3: Write minimal implementation**

Modify `src/PrintBit.Shared/Configurations/HardwareSettings.cs`:
```csharp
namespace PrintBit.Shared.Configurations;

public class HardwareSettings
{
    public string Esp32Port { get; set; } = "COM3";
    public int Esp32BaudRate { get; set; } = 115200;
    public int PrintTimeoutSeconds { get; set; } = 120;
    public string PrinterName { get; set; } = "EPSON L5290 Series";
    public string PrintQueueDirectory { get; set; } = "queue";
    public string? FailedDirectory { get; set; }
    public string SumatraPath { get; set; } = @"C:\Users\Admin\Desktop\printbit\bin\SumatraPDF.exe";
    public string QpdfPath { get; set; } = @"C:\Users\Admin\Desktop\printbit\bin\qpdf.exe";
    public int PdfSplitTimeoutSeconds { get; set; } = 30;
    public int PauseTimeoutMinutes { get; set; } = 15;
}
```

Modify `src/PrintBit.Infrastructure/IPC/WorkerPrintEventType.cs`:
```csharp
namespace PrintBit.Infrastructure.IPC;

public enum WorkerPrintEventType
{
    PrintStarted = 0,
    PrintSucceeded = 1,
    PrintFailed = 2,
    PrinterOffline = 3,
    PrinterOnline = 4,
    PrinterError = 5,
    PrintProgress = 6,
    JobPaused = 7,
    JobResumed = 8,
    JobCompleted = 9
}
```

Create `src/PrintBit.Shared/Printing/PagePrintState.cs`:
```csharp
namespace PrintBit.Shared.Printing;

public enum PagePrintState
{
    Pending,
    Printing,
    Completed,
    Failed,
    Cancelled
}
```

Create `src/PrintBit.Shared/Printing/PagePrintEntry.cs`:
```csharp
using System;

namespace PrintBit.Shared.Printing;

public class PagePrintEntry
{
    public int PageNumber { get; init; }
    public int CopyNumber { get; init; }
    public int SequenceIndex { get; init; }
    public PagePrintState State { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
```

Modify `src/PrintBit.Infrastructure/IPC/WorkerPrintEvent.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PrintBit.Infrastructure.IPC;

public sealed class WorkerPrintPageResult
{
    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("copy")]
    public int Copy { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty; // "completed", "cancelled", "failed"
}

public sealed record WorkerPrintEvent
{
    public WorkerPrintEventType Type { get; init; }
    public string? TransactionId { get; init; }
    public string? SpoolerCorrelationKey { get; init; }
    public string? SpoolerJobId { get; init; }
    public string? FileName { get; init; }
    public string? PrinterName { get; init; }
    public string? FailureStage { get; init; }
    public string? Message { get; init; }
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public int? PagesPrinted { get; init; }
    public int? TotalPages { get; init; }

    public int? PageNumber { get; init; }
    public int? CopyNumber { get; init; }
    public int? FailedPageNumber { get; init; }
    public int? FailedCopyNumber { get; init; }
    public int? ResumingPageNumber { get; init; }
    public int? ResumingCopyNumber { get; init; }
    public int? CompletedCount { get; init; }
    public int? TotalCount { get; init; }
    public string? Outcome { get; init; }
    public int? TotalCopies { get; init; }
    public int? TotalExpected { get; init; }
    public int? CancelledCount { get; init; }
    public int? FailedCount { get; init; }
    public List<WorkerPrintPageResult>? Pages { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? ErrorMessage { get; init; }
}
```

- [ ] **Step 4: Run test to verify it passes**
Run: `dotnet test --filter "FullyQualifiedName~WorkerPrintEventTests"`
Expected: PASS

- [ ] **Step 5: Commit**
```bash
git add src/PrintBit.Shared/Configurations/HardwareSettings.cs src/PrintBit.Infrastructure/IPC/WorkerPrintEventType.cs src/PrintBit.Infrastructure/IPC/WorkerPrintEvent.cs src/PrintBit.Shared/Printing/PagePrintState.cs src/PrintBit.Shared/Printing/PagePrintEntry.cs tests/PrintBit.Tests/WorkerPrintEventTests.cs
git commit -m "feat: add page print state models and update configurations"
```

---

### Task 2: Create Printer Health Monitor Abstraction & Unified Implementation

This task unifies general status monitoring, WMI polling, Win32 P/Invokes, and offline/online/error events.

**Files:**
- Create: `src/PrintBit.Infrastructure/Services/PrintService/IPrinterHealthMonitor.cs`
- Create: `src/PrintBit.Infrastructure.Windows/PrinterMonitoring/PrinterHealthMonitor.cs`
- Test: `tests/PrintBit.Tests/PrinterHealthMonitorTests.cs`

- [ ] **Step 1: Write the interface IPrinterHealthMonitor**
Create `src/PrintBit.Infrastructure/Services/PrintService/IPrinterHealthMonitor.cs`:
```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PrintBit.Infrastructure.Services.PrintService;

public interface IPrinterHealthMonitor
{
    bool IsHealthy(string printerName, out int winSpoolStatus, out string winSpoolDesc);
    bool HasFatalHardwareError(string printerName, out int errorCode, out string errorMessage);
    Task<bool> WaitForPrinterHealthyAsync(
        string printerName,
        int timeoutSeconds,
        CancellationToken cancellationToken);
    Task RecoverAsync(CancellationToken cancellationToken);
    (bool JobExists, uint StatusMask, string JobStatus, int PagesPrinted, int TotalPages) QueryJobStatus(
        string printerName,
        string documentName);
    void CancelMatchingJobs(string printerName, string documentName, string? spoolerJobId);
}
```

- [ ] **Step 2: Write failing unit test for PrinterHealthMonitor**
Create `tests/PrintBit.Tests/PrinterHealthMonitorTests.cs`:
```csharp
using Xunit;
using Moq;
using PrintBit.Infrastructure.Services.PrintService;
using System.Threading;
using System.Threading.Tasks;

namespace PrintBit.Tests;

public class PrinterHealthMonitorTests
{
    [Fact]
    public async Task WaitForPrinterHealthyAsync_StopsOnCancellation()
    {
        var monitorMock = new Mock<IPrinterHealthMonitor>();
        monitorMock.Setup(m => m.WaitForPrinterHealthyAsync(
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await monitorMock.Object.WaitForPrinterHealthyAsync("Printer", 30, cts.Token);
        Assert.False(result);
    }
}
```

- [ ] **Step 3: Run tests to verify failure**
Run: `dotnet test --filter "FullyQualifiedName~PrinterHealthMonitorTests"`
Expected: Compilation errors or failed test if run.

- [ ] **Step 4: Implement PrinterHealthMonitor**
Create `src/PrintBit.Infrastructure.Windows/PrinterMonitoring/PrinterHealthMonitor.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;

namespace PrintBit.Infrastructure.Windows.PrinterMonitoring;

[SupportedOSPlatform("windows")]
public class PrinterHealthMonitor : BackgroundService, IPrinterHealthMonitor
{
    private readonly ILogger<PrinterHealthMonitor> _logger;
    private readonly HardwareSettings _hardwareSettings;
    private readonly WorkerEventPipeClient _eventPipe;
    private readonly object _lock = new();

    private bool? _lastOfflineState = null;
    private string? _lastErrorState = null;
    private WorkerPrintEvent? _pendingEvent;
    
    private int _fatalErrorCode = 0;
    private string _fatalErrorMessage = string.Empty;

    // Win32 APIs for Epson Status Monitor Popup checking
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private static readonly string[] EpsonErrorKeywords = 
    {
        "out of paper", "jam", "ink out", "no ink", "door open", "cover open", "offline", "error", "service"
    };

    public PrinterHealthMonitor(
        ILogger<PrinterHealthMonitor> logger,
        IOptions<HardwareSettings> hardwareOptions,
        WorkerEventPipeClient eventPipe)
    {
        _logger = logger;
        _hardwareSettings = hardwareOptions.Value;
        _eventPipe = eventPipe;
    }

    public bool IsHealthy(string printerName, out int winSpoolStatus, out string winSpoolDesc)
    {
        var winSpoolOk = WinSpoolApi.GetPrinterStatus(printerName, out winSpoolStatus, out winSpoolDesc);
        var wsHealthy = winSpoolOk && ((winSpoolStatus & WinSpoolApi.FATAL_STATUS_MASK) == 0);
        var wmiHealthy = IsWmiHealthy(printerName, out _);
        var (hasPopup, _, _) = CheckEpsonStatusMonitorPopup();

        return wsHealthy && wmiHealthy && !hasPopup;
    }

    public bool HasFatalHardwareError(string printerName, out int errorCode, out string errorMessage)
    {
        lock (_lock)
        {
            if (_fatalErrorCode != 0)
            {
                errorCode = _fatalErrorCode;
                errorMessage = _fatalErrorMessage;
                return true;
            }
        }

        var winSpoolOk = WinSpoolApi.GetPrinterStatus(printerName, out var winSpoolStatus, out var winSpoolDesc);
        if (winSpoolOk && (winSpoolStatus & WinSpoolApi.FATAL_STATUS_MASK) != 0)
        {
            errorCode = (int)winSpoolStatus;
            errorMessage = $"WinSpool reports: {winSpoolDesc}";
            return true;
        }

        if (!IsWmiHealthy(printerName, out var wmiDesc))
        {
            errorCode = 3;
            errorMessage = wmiDesc;
            return true;
        }

        var (hasPopup, pid, title) = CheckEpsonStatusMonitorPopup();
        if (hasPopup)
        {
            errorCode = 99;
            errorMessage = $"Epson Popup: {title} (PID {pid})";
            return true;
        }

        errorCode = 0;
        errorMessage = string.Empty;
        return false;
    }

    public async Task<bool> WaitForPrinterHealthyAsync(
        string printerName,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Waiting for printer '{printer}' (timeout: {timeout}s)", printerName, timeoutSeconds);
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline || timeoutSeconds == 0)
        {
            if (cancellationToken.IsCancellationRequested) return false;

            if (IsHealthy(printerName, out var status, out var desc))
            {
                _logger.LogInformation("Printer '{printer}' is healthy.", printerName);
                return true;
            }

            _logger.LogWarning("Printer unhealthy: wsStatus=0x{status:X} ({desc}). Nudging...", status, desc);
            _ = WinSpoolApi.SetPrinterStatusReset(printerName);
            _ = WinSpoolApi.NudgePrinter(printerName);

            if (timeoutSeconds == 0) return false; // immediate check

            await Task.Delay(2000, cancellationToken);
        }
        return false;
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogWarning("Executing printer recovery spooler restarts...");
            KillProcess("SumatraPDF");
            KillProcess("E_YARNYRE");
            await RestartSpoolerAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovery failed");
        }
    }

    public (bool JobExists, uint StatusMask, string JobStatus, int PagesPrinted, int TotalPages) QueryJobStatus(
        string printerName,
        string documentName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Document, StatusMask, JobStatus, JobId, PagesPrinted, TotalPages FROM Win32_PrintJob");

            foreach (ManagementObject job in searcher.Get().Cast<ManagementObject>())
            {
                var jobName = job["Name"]?.ToString() ?? string.Empty;
                var document = job["Document"]?.ToString() ?? string.Empty;

                if (jobName.StartsWith(printerName, StringComparison.OrdinalIgnoreCase) &&
                    document.Contains(documentName, StringComparison.OrdinalIgnoreCase))
                {
                    var mask = Convert.ToUInt32(job["StatusMask"] ?? 0u);
                    var status = job["JobStatus"]?.ToString() ?? string.Empty;
                    var printed = Convert.ToInt32(job["PagesPrinted"] ?? 0);
                    var total = Convert.ToInt32(job["TotalPages"] ?? 0);
                    return (true, mask, status, printed, total);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query print job status");
        }
        return (false, 0, string.Empty, 0, 0);
    }

    public void CancelMatchingJobs(string printerName, string documentName, string? spoolerJobId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Document, JobStatus, JobId FROM Win32_PrintJob");

            foreach (ManagementObject job in searcher.Get().Cast<ManagementObject>())
            {
                var jobName = job["Name"]?.ToString() ?? string.Empty;
                var document = job["Document"]?.ToString() ?? string.Empty;
                var jobId = Convert.ToUInt32(job["JobId"] ?? 0u).ToString();

                if (jobName.StartsWith(printerName, StringComparison.OrdinalIgnoreCase) &&
                    (document.Contains(documentName, StringComparison.OrdinalIgnoreCase) || jobId == spoolerJobId))
                {
                    _logger.LogWarning("Deleting stuck print job {jobId}: {doc}", jobId, document);
                    job.Delete();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel print job");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Unified Printer Monitor loop started for {printer}", _hardwareSettings.PrinterName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MonitorPrinterAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in health monitoring loop");
            }
            await Task.Delay(2000, stoppingToken);
        }
    }

    private async Task MonitorPrinterAsync(CancellationToken stoppingToken)
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT DetectedErrorState, WorkOffline FROM Win32_Printer WHERE Name = '{_hardwareSettings.PrinterName}'");

        foreach (ManagementObject printer in searcher.Get().Cast<ManagementObject>())
        {
            var isOffline = printer["WorkOffline"] is true;
            var errorStateRaw = printer["DetectedErrorState"]?.ToString() ?? "0";
            var errorCode = int.TryParse(errorStateRaw, out var c) ? c : 0;

            if (_lastOfflineState != isOffline)
            {
                _lastOfflineState = isOffline;
                _pendingEvent = new WorkerPrintEvent
                {
                    Type = isOffline ? WorkerPrintEventType.PrinterOffline : WorkerPrintEventType.PrinterOnline,
                    PrinterName = _hardwareSettings.PrinterName,
                    Message = isOffline ? "Printer offline" : "Printer back online"
                };
            }

            lock (_lock)
            {
                if (errorCode >= 3)
                {
                    _fatalErrorCode = errorCode;
                    _fatalErrorMessage = DetectedErrorStateDescription(errorCode);

                    if (_lastErrorState != errorStateRaw)
                    {
                        _lastErrorState = errorStateRaw;
                        _pendingEvent = new WorkerPrintEvent
                        {
                            Type = WorkerPrintEventType.PrinterError,
                            PrinterName = _hardwareSettings.PrinterName,
                            FailureStage = "hardware_error",
                            Message = $"Printer error ({_fatalErrorMessage})"
                        };
                    }
                }
                else
                {
                    _fatalErrorCode = 0;
                    _fatalErrorMessage = string.Empty;
                    _lastErrorState = null;
                }
            }
        }

        if (_pendingEvent is not null && await _eventPipe.SendAsync(_pendingEvent, stoppingToken))
        {
            _pendingEvent = null;
        }
    }

    private bool IsWmiHealthy(string printerName, out string wmiDesc)
    {
        wmiDesc = "OK";
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT DetectedErrorState, ExtendedPrinterStatus, WorkOffline FROM Win32_Printer WHERE Name = '{printerName}'");
            
            foreach (ManagementObject printer in searcher.Get().Cast<ManagementObject>())
            {
                if (printer["WorkOffline"] is true)
                {
                    wmiDesc = "Offline";
                    return false;
                }
                var err = Convert.ToInt32(printer["DetectedErrorState"] ?? 0);
                if (err >= 3)
                {
                    wmiDesc = $"Error {err} ({DetectedErrorStateDescription(err)})";
                    return false;
                }
            }
        }
        catch
        {
            // best effort
        }
        return true;
    }

    private static string DetectedErrorStateDescription(int code) => code switch
    {
        3 => "Low Paper",
        4 => "No Paper",
        5 => "Low Toner",
        6 => "No Toner",
        7 => "Door Open",
        8 => "Jammed",
        9 => "Offline",
        10 => "Service Requested",
        11 => "Output Bin Full",
        _ => $"Unknown Error ({code})"
    };

    private (bool HasPopup, int ProcessId, string WindowTitle) CheckEpsonStatusMonitorPopup()
    {
        bool found = false;
        int targetPid = 0;
        string foundTitle = string.Empty;

        try
        {
            _ = EnumWindows((hWnd, lParam) =>
            {
                if (IsWindowVisible(hWnd))
                {
                    var sb = new StringBuilder(256);
                    _ = GetWindowText(hWnd, sb, 256);
                    string title = sb.ToString();

                    if (title.StartsWith("EPSON Status Monitor 3", StringComparison.OrdinalIgnoreCase))
                    {
                        var textBuilder = new StringBuilder();
                        textBuilder.AppendLine(title);

                        _ = EnumChildWindows(hWnd, (childHwnd, childLParam) =>
                        {
                            if (IsWindowVisible(childHwnd))
                            {
                                var childSb = new StringBuilder(256);
                                GetWindowText(childHwnd, childSb, 256);
                                if (childSb.Length > 0)
                                {
                                    textBuilder.AppendLine(childSb.ToString());
                                }
                            }
                            return true;
                        }, IntPtr.Zero);

                        string fullContent = textBuilder.ToString();
                        bool isError = EpsonErrorKeywords.Any(k => fullContent.Contains(k, StringComparison.OrdinalIgnoreCase));

                        if (isError)
                        {
                            _ = GetWindowThreadProcessId(hWnd, out uint pid);
                            found = true;
                            targetPid = (int)pid;
                            foundTitle = title;
                            return false;
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // ignored
        }
        return (found, targetPid, foundTitle);
    }

    private void KillProcess(string name)
    {
        foreach (var p in Process.GetProcessesByName(name))
        {
            try { p.Kill(true); } catch { }
        }
    }

    private async Task RestartSpoolerAsync()
    {
        using var stop = Process.Start(new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/c net stop spooler", CreateNoWindow = true, UseShellExecute = false });
        if (stop is not null) await stop.WaitForExitAsync();
        await Task.Delay(2000);
        using var start = Process.Start(new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/c net start spooler", CreateNoWindow = true, UseShellExecute = false });
        if (start is not null) await start.WaitForExitAsync();
    }
}
```

- [ ] **Step 5: Run tests and verify they pass**
Run: `dotnet test --filter "FullyQualifiedName~PrinterHealthMonitorTests"`
Expected: PASS

- [ ] **Step 6: Commit**
```bash
git add src/PrintBit.Infrastructure/Services/PrintService/IPrinterHealthMonitor.cs src/PrintBit.Infrastructure.Windows/PrinterMonitoring/PrinterHealthMonitor.cs tests/PrintBit.Tests/PrinterHealthMonitorTests.cs
git commit -m "feat: implement unified PrinterHealthMonitor service"
```

---

### Task 3: Implement Single-Page Printer

PagePrinter coordinates SumatraPDF process starts and spooler verification (Patience Mode wait and Cancel/Stop signal checking).

**Files:**
- Create: `src/PrintBit.Infrastructure/Services/PrintService/PagePrintResult.cs`
- Create: `src/PrintBit.Infrastructure/Services/PrintService/IPagePrinter.cs`
- Create: `src/PrintBit.Infrastructure/Services/PrintService/PagePrinter.cs`
- Test: `tests/PrintBit.Tests/PagePrinterTests.cs`

- [ ] **Step 1: Write IPagePrinter and PagePrintResult**
Create `src/PrintBit.Infrastructure/Services/PrintService/PagePrintResult.cs`:
```csharp
using PrintBit.Shared.Printing;

namespace PrintBit.Infrastructure.Services.PrintService;

public class PagePrintResult
{
    public PagePrintState State { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SpoolerJobId { get; set; }
    public PrintFailureStage FailureStage { get; set; }
}
```

Create `src/PrintBit.Infrastructure/Services/PrintService/IPagePrinter.cs`:
```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PrintBit.Infrastructure.Services.PrintService;

public interface IPagePrinter
{
    Task<PagePrintResult> PrintPageAsync(
        string filePath,
        string printerName,
        int sequenceIndex,
        Action<string> onPaused,
        Action onResumed,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Write failing unit test for PagePrinter**
Create `tests/PrintBit.Tests/PagePrinterTests.cs`:
```csharp
using Xunit;
using Moq;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Printing;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PrintBit.Tests;

public class PagePrinterTests
{
    [Fact]
    public async Task PrintPageAsync_FileDoesNotExist_ReturnsFailedState()
    {
        var healthMock = new Mock<IPrinterHealthMonitor>();
        var settings = new PrintBit.Shared.Configurations.HardwareSettings { SumatraPath = "Sumatra.exe" };
        var options = Microsoft.Extensions.Options.Options.Create(settings);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<PagePrinter>.Instance;

        var sut = new PagePrinter(logger, options, healthMock.Object);

        var result = await sut.PrintPageAsync(
            "nonexistent.pdf",
            "PrinterName",
            0,
            _ => {},
            () => {},
            CancellationToken.None);

        Assert.Equal(PagePrintState.Failed, result.State);
        Assert.Equal(PrintFailureStage.Validation, result.FailureStage);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**
Run: `dotnet test --filter "FullyQualifiedName~PagePrinterTests"`
Expected: FAIL with compilation errors.

- [ ] **Step 4: Implement PagePrinter**
Create `src/PrintBit.Infrastructure/Services/PrintService/PagePrinter.cs`:
```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Shared.Configurations;
using PrintBit.Shared.Printing;

namespace PrintBit.Infrastructure.Services.PrintService;

public class PagePrinter : IPagePrinter
{
    private static readonly SemaphoreSlim PrintLock = new(1, 1);
    private readonly ILogger<PagePrinter> _logger;
    private readonly HardwareSettings _settings;
    private readonly IPrinterHealthMonitor _healthMonitor;

    public PagePrinter(
        ILogger<PagePrinter> logger,
        IOptions<HardwareSettings> options,
        IPrinterHealthMonitor healthMonitor)
    {
        _logger = logger;
        _settings = options.Value;
        _healthMonitor = healthMonitor;
    }

    public async Task<PagePrintResult> PrintPageAsync(
        string filePath,
        string printerName,
        int sequenceIndex,
        Action<string> onPaused,
        Action onResumed,
        CancellationToken cancellationToken)
    {
        await PrintLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(filePath))
            {
                return new PagePrintResult { State = PagePrintState.Failed, FailureStage = PrintFailureStage.Validation, ErrorMessage = "Page file not found" };
            }

            var sumatraPath = _settings.SumatraPath;
            if (!File.Exists(sumatraPath))
            {
                return new PagePrintResult { State = PagePrintState.Failed, FailureStage = PrintFailureStage.Validation, ErrorMessage = "SumatraPDF executable not found" };
            }

            _logger.LogInformation("Dispatching SumatraPDF for page {filePath} on printer {printerName}", filePath, printerName);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = sumatraPath,
                    Arguments = $"-print-to \"{printerName}\" -silent \"{filePath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            try { process.Start(); }
            catch (Exception ex)
            {
                return new PagePrintResult { State = PagePrintState.Failed, FailureStage = PrintFailureStage.ProcessStart, ErrorMessage = ex.Message };
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.PrintTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(true); } catch { }
                await _healthMonitor.RecoverAsync(cancellationToken);
                return new PagePrintResult { State = PagePrintState.Failed, FailureStage = PrintFailureStage.Timeout, ErrorMessage = "Sumatra process timeout" };
            }

            if (process.ExitCode != 0)
            {
                var err = await process.StandardError.ReadToEndAsync(cancellationToken);
                return new PagePrintResult { State = PagePrintState.Failed, FailureStage = PrintFailureStage.ProcessExit, ErrorMessage = err };
            }

            // Verification Loop (Patience Mode)
            return await VerifySpoolerPageLifecycleAsync(printerName, Path.GetFileName(filePath), onPaused, onResumed, cancellationToken);
        }
        finally
        {
            PrintLock.Release();
        }
    }

    private async Task<PagePrintResult> VerifySpoolerPageLifecycleAsync(
        string printerName,
        string documentName,
        Action<string> onPaused,
        Action onResumed,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        var patienceDeadline = DateTime.UtcNow.AddMinutes(_settings.PauseTimeoutMinutes);
        
        bool inPatienceMode = false;
        bool observedActive = false;
        string? lastSpoolerJobId = null;

        while (DateTime.UtcNow < deadline || (inPatienceMode && DateTime.UtcNow < patienceDeadline))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (exists, statusMask, jobStatus, printed, total) = _healthMonitor.QueryJobStatus(printerName, documentName);
            if (exists)
            {
                observedActive = true;
                
                // StatusMask: 0x2 (ERROR), 0x40 (PAPEROUT)
                bool jobHasError = (statusMask & (0x2 | 0x40)) != 0;
                bool fatalMonitorError = _healthMonitor.HasFatalHardwareError(printerName, out _, out var fatalMsg);

                if (jobHasError || fatalMonitorError)
                {
                    if (!inPatienceMode)
                    {
                        inPatienceMode = true;
                        var errorMsg = fatalMonitorError ? fatalMsg : $"Spooler error status: {jobStatus} (0x{statusMask:X})";
                        onPaused(errorMsg);
                    }
                }
                else
                {
                    if (inPatienceMode)
                    {
                        inPatienceMode = false;
                        onResumed();
                        deadline = DateTime.UtcNow.AddSeconds(45); // reset normal verification timeout
                    }
                }
            }
            else
            {
                // Job cleared / not found
                if (observedActive)
                {
                    // If we previously saw it, and it completed successfully, it transitions through printed
                    // We run a 12s post-clear guard window
                    _logger.LogInformation("Job cleared from spooler; running post-clear hardware guard window");
                    await Task.Delay(12000, cancellationToken);

                    if (_healthMonitor.HasFatalHardwareError(printerName, out var code, out var msg))
                    {
                        return new PagePrintResult { State = PagePrintState.Failed, FailureStage = PrintFailureStage.HardwareError, ErrorMessage = $"Post-clear hardware error code {code}: {msg}" };
                    }
                    return new PagePrintResult { State = PagePrintState.Completed };
                }
                
                // Job was never observed
                if (_healthMonitor.IsHealthy(printerName, out _, out _))
                {
                    // Treated as printed fast
                    return new PagePrintResult { State = PagePrintState.Completed };
                }
            }

            await Task.Delay(2000, cancellationToken);
        }

        if (inPatienceMode)
        {
            _logger.LogWarning("Patience timeout exceeded. Cancelling spooler job.");
            _healthMonitor.CancelMatchingJobs(printerName, documentName, lastSpoolerJobId);
            return new PagePrintResult { State = PagePrintState.Cancelled, FailureStage = PrintFailureStage.Timeout, ErrorMessage = "Patience timeout exceeded" };
        }

        return new PagePrintResult { State = PagePrintState.Failed, FailureStage = PrintFailureStage.SpoolerVerification, ErrorMessage = "Job did not appear in spooler or clear successfully" };
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**
Run: `dotnet test --filter "FullyQualifiedName~PagePrinterTests"`
Expected: PASS

- [ ] **Step 6: Commit**
```bash
git add src/PrintBit.Infrastructure/Services/PrintService/PagePrintResult.cs src/PrintBit.Infrastructure/Services/PrintService/IPagePrinter.cs src/PrintBit.Infrastructure/Services/PrintService/PagePrinter.cs tests/PrintBit.Tests/PagePrinterTests.cs
git commit -m "feat: implement single-page Sumatra PagePrinter and verification"
```

---

### Task 4: Implement Job Orchestrator

The `JobOrchestrator` handles manifest collating, qpdf splitting, the page loop, pre-flight pause, progress/completed events, and cleanup.

**Files:**
- Create: `src/PrintBit.Application/Printing/IJobOrchestrator.cs`
- Create: `src/PrintBit.Application/Printing/JobOrchestrator.cs`
- Test: `tests/PrintBit.Tests/JobOrchestratorTests.cs`

- [ ] **Step 1: Write IJobOrchestrator**
Create `src/PrintBit.Application/Printing/IJobOrchestrator.cs`:
```csharp
using System.Threading;
using System.Threading.Tasks;
using PrintBit.Infrastructure.Services.PrintService;

namespace PrintBit.Application.Printing;

public interface IJobOrchestrator
{
    Task<PrintJobResult> ProcessJobAsync(
        PrintJobRequest request,
        string jsonFilePath,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Write failing unit test for JobOrchestrator**
Create `tests/PrintBit.Tests/JobOrchestratorTests.cs`:
```csharp
using Xunit;
using Moq;
using PrintBit.Application.Printing;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Infrastructure.IPC;
using System.Threading;
using System.Threading.Tasks;

namespace PrintBit.Tests;

public class JobOrchestratorTests
{
    [Fact]
    public async Task ProcessJobAsync_SplitTimeout_ReturnsFailure()
    {
        var healthMock = new Mock<IPrinterHealthMonitor>();
        var pagePrinterMock = new Mock<IPagePrinter>();
        var eventPipeMock = new Mock<WorkerEventPipeClient>(null, null);
        
        var settings = new PrintBit.Shared.Configurations.HardwareSettings { QpdfPath = "invalid_qpdf.exe" };
        var options = Microsoft.Extensions.Options.Options.Create(settings);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<JobOrchestrator>.Instance;

        var sut = new JobOrchestrator(logger, options, pagePrinterMock.Object, healthMock.Object, null!);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**
Run: `dotnet test --filter "FullyQualifiedName~JobOrchestratorTests"`
Expected: FAIL due to missing class.

- [ ] **Step 4: Implement JobOrchestrator**
Create `src/PrintBit.Application/Printing/JobOrchestrator.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;
using PrintBit.Shared.Printing;

namespace PrintBit.Application.Printing;

public class JobOrchestrator : IJobOrchestrator
{
    private readonly ILogger<JobOrchestrator> _logger;
    private readonly HardwareSettings _settings;
    private readonly IPagePrinter _pagePrinter;
    private readonly IPrinterHealthMonitor _healthMonitor;
    private readonly WorkerEventPipeClient _eventPipe;

    public JobOrchestrator(
        ILogger<JobOrchestrator> logger,
        IOptions<HardwareSettings> options,
        IPagePrinter pagePrinter,
        IPrinterHealthMonitor healthMonitor,
        WorkerEventPipeClient eventPipe)
    {
        _logger = logger;
        _settings = options.Value;
        _pagePrinter = pagePrinter;
        _healthMonitor = healthMonitor;
        _eventPipe = eventPipe;
    }

    public async Task<PrintJobResult> ProcessJobAsync(
        PrintJobRequest request,
        string jsonFilePath,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(request.FilePath);
        var (transactionId, spoolerCorrelationKey) = PrintJobFileName.TryParseCorrelation(fileName);

        // Pre-validate
        if (transactionId is null || spoolerCorrelationKey is null)
        {
            return PrintJobResult.Failed(PrintFailureStage.Validation, "Filename does not match the tx_spool layout");
        }

        var pdfPageCount = PdfPageCounter.Count(request.FilePath);
        if (pdfPageCount is null || pdfPageCount.Value == 0)
        {
            return PrintJobResult.Failed(PrintFailureStage.Validation, "Could not determine PDF page count or PDF is corrupt");
        }

        var workDir = Path.Combine(Path.GetDirectoryName(request.FilePath) ?? ".", ".work", $"{transactionId}_{spoolerCorrelationKey}");
        if (Directory.Exists(workDir))
        {
            Directory.Delete(workDir, true);
        }
        Directory.CreateDirectory(workDir);

        // Split pages via qpdf
        try
        {
            await SplitPdfPagesAsync(request.FilePath, workDir, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "qpdf split failed");
            CleanWorkDirectory(workDir);
            return PrintJobResult.Failed(PrintFailureStage.ProcessExit, $"qpdf split failed: {ex.Message}");
        }

        var pagesToPrint = GetPagesInRange(pdfPageCount.Value, request.Settings.PageRange);
        var totalCopies = Math.Max(1, request.Settings.Copies);
        
        // Build Manifest (Collated sequence)
        var manifest = new List<PagePrintEntry>();
        int sequenceIndex = 0;
        for (int c = 1; c <= totalCopies; c++)
        {
            foreach (var pageNum in pagesToPrint)
            {
                manifest.Add(new PagePrintEntry
                {
                    PageNumber = pageNum,
                    CopyNumber = c,
                    SequenceIndex = sequenceIndex++,
                    State = PagePrintState.Pending
                });
            }
        }

        var startedAt = DateTime.UtcNow;
        int completedCount = 0;
        int cancelledCount = 0;
        int failedCount = 0;
        string? failureMessage = null;
        PrintFailureStage finalFailureStage = PrintFailureStage.None;

        for (int i = 0; i < manifest.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = manifest[i];

            // 1. Pre-flight health check
            if (!_healthMonitor.IsHealthy(request.PrinterName, out _, out _))
            {
                await EnterPreFlightPauseAsync(request, entry, cancellationToken);
            }

            if (entry.State == PagePrintState.Cancelled)
            {
                CancelRemaining(manifest, i);
                break;
            }

            // Find split page file
            var pageFilePath = FindSplitPageFile(workDir, entry.PageNumber);
            if (pageFilePath is null || !File.Exists(pageFilePath))
            {
                entry.State = PagePrintState.Failed;
                entry.ErrorMessage = $"Split page file for page {entry.PageNumber} not found";
                failedCount++;
                CancelRemaining(manifest, i + 1);
                failureMessage = entry.ErrorMessage;
                finalFailureStage = PrintFailureStage.Validation;
                break;
            }

            entry.State = PagePrintState.Printing;
            entry.StartedAt = DateTime.UtcNow;

            var printResult = await _pagePrinter.PrintPageAsync(
                pageFilePath,
                request.PrinterName,
                entry.SequenceIndex,
                onPaused: (errMsg) => EmitJobPaused(transactionId, spoolerCorrelationKey, entry, completedCount, manifest.Count, errMsg),
                onResumed: () => EmitJobResumed(transactionId, spoolerCorrelationKey, entry, completedCount, manifest.Count),
                cancellationToken);

            entry.CompletedAt = DateTime.UtcNow;
            if (printResult.State == PagePrintState.Completed)
            {
                entry.State = PagePrintState.Completed;
                completedCount++;
                await EmitPrintProgress(transactionId, spoolerCorrelationKey, entry, completedCount, manifest.Count, cancellationToken);
            }
            else if (printResult.State == PagePrintState.Cancelled)
            {
                entry.State = PagePrintState.Cancelled;
                entry.ErrorMessage = printResult.ErrorMessage;
                cancelledCount++;
                CancelRemaining(manifest, i + 1);
                failureMessage = printResult.ErrorMessage;
                finalFailureStage = printResult.FailureStage;
                break;
            }
            else
            {
                entry.State = PagePrintState.Failed;
                entry.ErrorMessage = printResult.ErrorMessage;
                failedCount++;
                CancelRemaining(manifest, i + 1);
                failureMessage = printResult.ErrorMessage;
                finalFailureStage = printResult.FailureStage;
                break;
            }
        }

        var completedAt = DateTime.UtcNow;
        var outcome = "completed";
        if (failedCount > 0) outcome = "failed";
        else if (cancelledCount > 0 || manifest.Any(m => m.State == PagePrintState.Cancelled)) outcome = "partially_completed";

        var pageResults = manifest.Select(m => new WorkerPrintPageResult
        {
            Page = m.PageNumber,
            Copy = m.CopyNumber,
            State = m.State.ToString().ToLower()
        }).ToList();

        // Emit final JobCompleted event
        var finalEvent = new WorkerPrintEvent
        {
            Type = WorkerPrintEventType.JobCompleted,
            TransactionId = transactionId,
            SpoolerCorrelationKey = spoolerCorrelationKey,
            PrinterName = request.PrinterName,
            FileName = fileName,
            Outcome = outcome,
            TotalPages = pdfPageCount,
            TotalCopies = totalCopies,
            TotalExpected = manifest.Count,
            CompletedCount = completedCount,
            CancelledCount = manifest.Count(m => m.State == PagePrintState.Cancelled),
            FailedCount = failedCount,
            Pages = pageResults,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Message = outcome == "completed" ? "Print job completed successfully" : $"Print job finished with state: {outcome}. {failureMessage}"
        };

        if (_eventPipe is not null)
        {
            await _eventPipe.SendAsync(finalEvent, cancellationToken);
        }

        // Clean work directory
        CleanWorkDirectory(workDir);

        if (outcome == "failed")
        {
            return PrintJobResult.Failed(finalFailureStage, failureMessage ?? "Job execution failed");
        }

        return new PrintJobResult
        {
            Success = true,
            Message = $"Print job {outcome}",
            SumatraProcessSucceeded = true,
            VerificationSucceeded = true,
            PagesPrinted = completedCount,
            TotalPages = manifest.Count
        };
    }

    private async Task SplitPdfPagesAsync(string filePath, string workDir, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _settings.QpdfPath,
            Arguments = $"--split-pages \"{filePath}\" \"{Path.Combine(workDir, "page.pdf")}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        using var splitTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.PdfSplitTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, splitTimeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException("PDF split execution timed out");
        }

        if (process.ExitCode != 0)
        {
            var err = await process.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException($"qpdf split failed with exit code {process.ExitCode}: {err}");
        }
    }

    private static List<int> GetPagesInRange(int pdfPageCount, string? pageRange)
    {
        var pages = new List<int>();
        if (string.IsNullOrWhiteSpace(pageRange))
        {
            for (int i = 1; i <= pdfPageCount; i++) pages.Add(i);
            return pages;
        }

        var parts = pageRange.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (part.Contains('-'))
            {
                var subParts = part.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (subParts.Length == 2 && int.TryParse(subParts[0], out var start) && int.TryParse(subParts[1], out var end))
                {
                    var step = start <= end ? 1 : -1;
                    for (int i = start; start <= end ? i <= end : i >= end; i += step)
                    {
                        if (i >= 1 && i <= pdfPageCount) pages.Add(i);
                    }
                }
            }
            else
            {
                if (int.TryParse(part, out var p) && p >= 1 && p <= pdfPageCount) pages.Add(p);
            }
        }
        return pages;
    }

    private static string? FindSplitPageFile(string workDir, int pageNumber)
    {
        for (int length = 1; length <= 5; length++)
        {
            var padded = pageNumber.ToString().PadLeft(length, '0');
            var path = Path.Combine(workDir, $"page-{padded}.pdf");
            if (File.Exists(path)) return path;
        }
        var files = Directory.GetFiles(workDir, $"page-*{pageNumber}.pdf");
        return files.Length > 0 ? files[0] : null;
    }

    private void CancelRemaining(List<PagePrintEntry> manifest, int startIndex)
    {
        for (int i = startIndex; i < manifest.Count; i++)
        {
            manifest[i].State = PagePrintState.Cancelled;
        }
    }

    private async Task EnterPreFlightPauseAsync(
        PrintJobRequest request,
        PagePrintEntry entry,
        CancellationToken cancellationToken)
    {
        var (tx, sck) = PrintJobFileName.TryParseCorrelation(Path.GetFileName(request.FilePath));
        EmitJobPaused(tx, sck, entry, entry.SequenceIndex, entry.SequenceIndex + 1, "Printer unhealthy before page dispatch");

        var deadline = DateTime.UtcNow.AddMinutes(_settings.PauseTimeoutMinutes);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(2000, cancellationToken);

            if (_healthMonitor.IsHealthy(request.PrinterName, out _, out _))
            {
                EmitJobResumed(tx, sck, entry, entry.SequenceIndex, entry.SequenceIndex + 1);
                return;
            }
        }
        entry.State = PagePrintState.Cancelled;
        entry.ErrorMessage = "Pause timeout exceeded during pre-flight health wait";
    }

    private void EmitJobPaused(string? tx, string? sck, PagePrintEntry entry, int completed, int total, string reason)
    {
        var evt = new WorkerPrintEvent
        {
            Type = WorkerPrintEventType.JobPaused,
            TransactionId = tx,
            SpoolerCorrelationKey = sck,
            FailedPageNumber = entry.PageNumber,
            FailedCopyNumber = entry.CopyNumber,
            CompletedCount = completed,
            TotalCount = total,
            ErrorMessage = reason
        };
        _ = _eventPipe?.SendAsync(evt);
    }

    private void EmitJobResumed(string? tx, string? sck, PagePrintEntry entry, int completed, int total)
    {
        var evt = new WorkerPrintEvent
        {
            Type = WorkerPrintEventType.JobResumed,
            TransactionId = tx,
            SpoolerCorrelationKey = sck,
            ResumingPageNumber = entry.PageNumber,
            ResumingCopyNumber = entry.CopyNumber,
            CompletedCount = completed,
            TotalCount = total
        };
        _ = _eventPipe?.SendAsync(evt);
    }

    private async Task EmitPrintProgress(string? tx, string? sck, PagePrintEntry entry, int completed, int total, CancellationToken ct)
    {
        var evt = new WorkerPrintEvent
        {
            Type = WorkerPrintEventType.PrintProgress,
            TransactionId = tx,
            SpoolerCorrelationKey = sck,
            PageNumber = entry.PageNumber,
            CopyNumber = entry.CopyNumber,
            CompletedCount = completed,
            TotalCount = total
        };
        if (_eventPipe is not null)
        {
            await _eventPipe.SendAsync(evt, ct);
        }
    }

    private void CleanWorkDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to clean working directory {dir}", dir); }
    }
}
```

- [ ] **Step 5: Add mock dependencies for JobOrchestrator tests and make them pass**
Implement tests in `tests/PrintBit.Tests/JobOrchestratorTests.cs` (e.g. testing PDF range splits, collated order manifest verification). Let's make sure this builds and runs.

- [ ] **Step 6: Commit**
```bash
git add src/PrintBit.Application/Printing/IJobOrchestrator.cs src/PrintBit.Application/Printing/JobOrchestrator.cs tests/PrintBit.Tests/JobOrchestratorTests.cs
git commit -m "feat: implement JobOrchestrator to split PDF and dispatch page loop"
```

---

### Task 5: Implement Slimmed-Down Queue Watcher and Setup Hosted Services

**Files:**
- Create: `src/PrintBit.HardwareService/Services/PrintQueueWatcher.cs`
- Modify: `src/PrintBit.HardwareService/Program.cs`
- Test: `tests/PrintBit.Tests/WorkerPrintEventTests.cs`

- [ ] **Step 1: Write PrintQueueWatcher**
Create `src/PrintBit.HardwareService/Services/PrintQueueWatcher.cs` (slims down directory watching to delegate execution of JSON sidecars to `IJobOrchestrator` and handles cleanup folders):
```csharp
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintBit.Application.Printing;
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;
using PrintBit.Shared.Printing;

namespace PrintBit.HardwareService.Services;

public class PrintQueueWatcher : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly System.Collections.Generic.HashSet<string> _processingFiles = [];
    private readonly ILogger<PrintQueueWatcher> _logger;
    private readonly IJobOrchestrator _orchestrator;
    private readonly WorkerEventPipeClient _eventPipe;
    private readonly HardwareSettings _settings;

    public PrintQueueWatcher(
        ILogger<PrintQueueWatcher> logger,
        IJobOrchestrator orchestrator,
        WorkerEventPipeClient eventPipe,
        IOptions<HardwareSettings> options)
    {
        _logger = logger;
        _orchestrator = orchestrator;
        _eventPipe = eventPipe;
        _settings = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queueDirectory = Path.GetFullPath(_settings.PrintQueueDirectory);
        var failedDirectory = !string.IsNullOrWhiteSpace(_settings.FailedDirectory)
            ? Path.GetFullPath(_settings.FailedDirectory)
            : Path.Combine(Path.GetDirectoryName(queueDirectory) ?? AppContext.BaseDirectory, "failed");

        Directory.CreateDirectory(queueDirectory);
        Directory.CreateDirectory(failedDirectory);

        _logger.LogInformation("Watching print queue: {path}", queueDirectory);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var jsonFiles = Directory.GetFiles(queueDirectory, "*.json");
                foreach (var jsonFile in jsonFiles)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    if (_processingFiles.Contains(jsonFile) || !IsPrintJobSidecar(jsonFile)) continue;

                    _processingFiles.Add(jsonFile);
                    try
                    {
                        await Task.Delay(1000, stoppingToken);

                        var pdfFile = Path.ChangeExtension(jsonFile, ".pdf");
                        if (!File.Exists(pdfFile))
                        {
                            _logger.LogWarning("Found JSON sidecar {jsonFile} but missing PDF file. Moving to failed.", jsonFile);
                            File.Move(jsonFile, Path.Combine(failedDirectory, Path.GetFileName(jsonFile)), true);
                            continue;
                        }

                        _logger.LogInformation("Detected print job: {pdfFile}", pdfFile);
                        var jsonContent = await File.ReadAllTextAsync(jsonFile, stoppingToken);
                        var printSettings = JsonSerializer.Deserialize<PrintJobSettings>(jsonContent, JsonOptions) ?? new PrintJobSettings();

                        var (txId, spoolKey) = PrintJobFileName.TryParseCorrelation(Path.GetFileName(pdfFile));

                        // Emit PrintStarted
                        await _eventPipe.SendAsync(new WorkerPrintEvent
                        {
                            Type = WorkerPrintEventType.PrintStarted,
                            TransactionId = txId,
                            SpoolerCorrelationKey = spoolKey,
                            FileName = Path.GetFileName(pdfFile),
                            PrinterName = _settings.PrinterName
                        }, stoppingToken);

                        var request = new PrintJobRequest
                        {
                            FilePath = pdfFile,
                            PrinterName = _settings.PrinterName,
                            Settings = printSettings
                        };

                        var result = await _orchestrator.ProcessJobAsync(request, jsonFile, stoppingToken);
                        if (result.Success)
                        {
                            File.Delete(pdfFile);
                            File.Delete(jsonFile);
                            _logger.LogInformation("Print job processed successfully. Cleared sidecars.");
                        }
                        else
                        {
                            File.Move(pdfFile, Path.Combine(failedDirectory, Path.GetFileName(pdfFile)), true);
                            File.Move(jsonFile, Path.Combine(failedDirectory, Path.GetFileName(jsonFile)), true);
                            _logger.LogWarning("Print job failed. Files moved to failed directory.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process queue file {file}", jsonFile);
                    }
                    finally
                    {
                        _processingFiles.Remove(jsonFile);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Print queue watcher loop hit error");
            }

            await Task.Delay(2000, stoppingToken);
        }
    }

    private static bool IsPrintJobSidecar(string jsonFile)
    {
        var fileName = Path.GetFileName(jsonFile);
        var (tx, spool) = PrintJobFileName.TryParseCorrelation(fileName);
        return tx is not null && spool is not null;
    }
}
```

- [ ] **Step 2: Update DI Registrations**
Modify `src/PrintBit.HardwareService/Program.cs` to register the new services.
Replace lines 15-25:
```csharp
builder.Services.AddHostedService<ErrorPipeHostedService>();

// Registrations for the new page-level spooler dispatch model
builder.Services.AddSingleton<PrinterHealthMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PrinterHealthMonitor>());
builder.Services.AddSingleton<IPrinterHealthMonitor>(sp => sp.GetRequiredService<PrinterHealthMonitor>());

builder.Services.AddSingleton<IPagePrinter, PagePrinter>();
builder.Services.AddSingleton<IJobOrchestrator, JobOrchestrator>();
builder.Services.AddHostedService<PrintQueueWatcher>();

builder.Services.AddSingleton<WorkerEventPipeClient>();
```

- [ ] **Step 3: Run project build**
Run: `dotnet build`
Expected: SUCCESS

- [ ] **Step 4: Commit**
```bash
git add src/PrintBit.HardwareService/Services/PrintQueueWatcher.cs src/PrintBit.HardwareService/Program.cs
git commit -m "feat: setup Hosted Services and slimmed down PrintQueueWatcher"
```

---

### Task 6: Remove Obsolete Code Files

Clean up files replaced by the new architecture.

- [ ] **Step 1: Delete obsolete files**
Delete:
- `src/PrintBit.Infrastructure/Services/PrintService/IPrintService.cs`
- `src/PrintBit.Infrastructure/Services/PrintService/PrintService.cs`
- `src/PrintBit.Infrastructure/Services/PrintService/IPrintRecoveryService.cs`
- `src/PrintBit.Infrastructure/Services/PrintService/PrintRecoveryService.cs`
- `src/PrintBit.Infrastructure/Services/PrintService/IPrintHealthCoordinator.cs`
- `src/PrintBit.Infrastructure/Services/PrintService/PrintHealthCoordinator.cs`
- `src/PrintBit.Infrastructure.Windows/PrinterMonitoring/PrinterMonitorService.cs`
- `src/PrintBit.HardwareService/Services/PrintQueueWatcherService.cs`
- `tests/PrintBit.Tests/PrintServiceTests.cs`
- `tests/PrintBit.Tests/PrintServiceIntegrationTests.cs`

- [ ] **Step 2: Verify project builds successfully**
Run: `dotnet build`
Expected: SUCCESS

- [ ] **Step 3: Commit deletion**
```bash
git rm src/PrintBit.Infrastructure/Services/PrintService/IPrintService.cs src/PrintBit.Infrastructure/Services/PrintService/PrintService.cs src/PrintBit.Infrastructure/Services/PrintService/IPrintRecoveryService.cs src/PrintBit.Infrastructure/Services/PrintService/PrintRecoveryService.cs src/PrintBit.Infrastructure/Services/PrintService/IPrintHealthCoordinator.cs src/PrintBit.Infrastructure/Services/PrintService/PrintHealthCoordinator.cs src/PrintBit.Infrastructure.Windows/PrinterMonitoring/PrinterMonitorService.cs src/PrintBit.HardwareService/Services/PrintQueueWatcherService.cs tests/PrintBit.Tests/PrintServiceTests.cs tests/PrintBit.Tests/PrintServiceIntegrationTests.cs
git commit -m "refactor: remove obsolete multi-page print service and coordinator files"
```

---

### Task 7: Execute Global Verification Suite

Ensure that all tests compile, old assertions are properly clean, and 100% of tests pass.

- [ ] **Step 1: Run complete unit test suite**
Run: `dotnet test`
Expected: PASS with 0 failures

- [ ] **Step 2: Save final verification results**
Check output carefully and ensure all warnings/errors are addressed.
