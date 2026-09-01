# Printer Outage Alternative Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a secure, admin-triggered printer recovery control plane that diagnoses the configured Epson queue and can perform one bounded Windows Print Spooler restart without manipulating print jobs or kiosk restrictions.

**Architecture:** A dedicated command pipe accepts only `GetPrinterRecoveryStatus` and `AttemptPrinterRecovery` requests from the local administrator Node service. A Windows-only recovery service combines a typed printer diagnostic with a native Spooler controller, while a singleton operation coordinator prevents recovery from racing an active print. Recovery results are synchronous, correlated, and persisted by Node; the worker remains stateless.

**Tech Stack:** .NET 10 Windows Worker, `System.ServiceProcess.ServiceController` 10.0.8, `System.IO.Pipes`/`PipeSecurity`, WMI/WinSpool health checks, `System.Text.Json`, xUnit, Moq.

**Spec:** `docs/superpowers/specs/printer-outage-alternative.md`

## Global Constraints

- The worker command pipe grants access only to `LocalSystem` and `BUILTIN\\Administrators`; it never grants `World` or `Authenticated Users` access.
- v1 exposes diagnosis and one Spooler restart only; it does not resume/cancel jobs, reset PnP devices, purge spool files, launch Epson utilities, or kill processes.
- Physical Epson faults (paper out, jam, door/cover, ink, service/Epson popup faults) return `manual_intervention_required` without restarting Spooler.
- Recovery returns `worker_busy` while the worker owns an active print; it never interrupts that print.
- `DocumentPrinter` keeps its existing `SemaphoreSlim(1, 1)`, 120-second print timeout, whole-document dispatch, and verification semantics.
- All hardware services remain singleton; no `Infrastructure -> Application` reference is introduced.
- Node owns maintenance UI authorization, customer-transaction blocking, response timeout, and durable recovery history.
- `AGENTS.md` must be updated for every changed public interface, DI registration, configuration key, DACL behavior, and recovery policy.

---

### Task 1: Define recovery contracts and operation coordination

**Files:**
- Create: `src/PrintBit.Infrastructure/Services/PrintService/PrinterRecoveryContracts.cs`
- Create: `src/PrintBit.Infrastructure/Services/PrintService/IPrinterRecoveryService.cs`
- Create: `src/PrintBit.Infrastructure/Services/PrintService/IPrinterOperationCoordinator.cs`
- Create: `src/PrintBit.Infrastructure/Services/PrintService/PrintOperationCoordinator.cs`
- Create: `tests/PrintBit.Tests/PrinterRecoveryContractsTests.cs`

**Interfaces:**
- `IPrinterRecoveryService.GetStatusAsync(CancellationToken)` and `AttemptRepairAsync(CancellationToken)` return `Task<PrinterRecoveryResult>`.
- `IPrinterOperationCoordinator.AcquirePrintAsync(CancellationToken)` returns `Task<IDisposable>`; `TryAcquireRecovery(out IDisposable? lease)` returns `false` when a print or another recovery owns the lease.
- `PrinterRecoveryCommandType` values are `GetPrinterRecoveryStatus` and `AttemptPrinterRecovery`.
- `PrinterRecoveryOutcome` values are `healthy`, `recovered`, `manual_intervention_required`, `worker_busy`, `restart_failed`, and `invalid_request` (serialized as explicit lowercase strings).
- `PrinterRecoveryCommand` contains `RequestId`, `Type`, and `TimestampUtc`; it contains no printer, file, process, or job identifier.
- `PrinterRecoveryResult` contains `RequestId`, `Type`, `Outcome`, `Action`, `SpoolerState`, `PrinterState`, `IssueKind`, `Message`, `StartedAt`, and `CompletedAt`.

- [ ] **Step 1: Write failing coordinator tests**

```csharp
[Fact]
public async Task TryAcquireRecovery_ReturnsBusyWhilePrintLeaseIsHeld()
{
    var coordinator = new PrintOperationCoordinator();
    using var printLease = await coordinator.AcquirePrintAsync(CancellationToken.None);

    var acquired = coordinator.TryAcquireRecovery(out var recoveryLease);

    Assert.False(acquired);
    Assert.Null(recoveryLease);
}

[Fact]
public void TryAcquireRecovery_AllowsOnlyOneRecoveryLease()
{
    var coordinator = new PrintOperationCoordinator();

    Assert.True(coordinator.TryAcquireRecovery(out var first));
    Assert.False(coordinator.TryAcquireRecovery(out var second));
    Assert.NotNull(first);
    Assert.Null(second);
}
```

- [ ] **Step 2: Run the focused tests and verify they fail**

Run: `dotnet test tests/PrintBit.Tests/PrintBit.Tests.csproj --filter FullyQualifiedName~PrinterRecoveryContractsTests`

Expected: FAIL because the coordinator types do not yet exist.

- [ ] **Step 3: Implement contracts, explicit JSON enum names, and a semaphore-backed coordinator**

Use one `SemaphoreSlim(1, 1)`. Print acquisition awaits the semaphore; recovery uses `Wait(0)` and returns a disposable lease that releases exactly once. Dispose the semaphore from `PrintOperationCoordinator.Dispose()` after hosted services stop.

- [ ] **Step 4: Run the focused tests and the existing suite**

Run: `dotnet test tests/PrintBit.Tests/PrintBit.Tests.csproj --filter FullyQualifiedName~PrinterRecoveryContractsTests`

Expected: PASS.

Run: `dotnet test --no-restore`

Expected: 75 existing tests plus the new coordinator tests pass.

- [ ] **Step 5: Commit the contract slice**

```bash
git add src/PrintBit.Infrastructure/Services/PrintService/PrinterRecoveryContracts.cs src/PrintBit.Infrastructure/Services/PrintService/IPrinterRecoveryService.cs src/PrintBit.Infrastructure/Services/PrintService/IPrinterOperationCoordinator.cs src/PrintBit.Infrastructure/Services/PrintService/PrintOperationCoordinator.cs src/PrintBit.Shared/Configurations/IpcSettings.cs tests/PrintBit.Tests/PrinterRecoveryContractsTests.cs
git commit -m "feat: add printer recovery contracts and operation gate"
```

### Task 2: Expose a typed printer diagnostic

**Files:**
- Create: `src/PrintBit.Infrastructure/Services/PrintService/PrinterHealthDiagnostic.cs`
- Modify: `src/PrintBit.Infrastructure/Services/PrintService/IPrinterHealthMonitor.cs`
- Modify: `src/PrintBit.Infrastructure.Windows/PrinterMonitoring/PrinterHealthMonitor.cs`
- Modify: `tests/PrintBit.Tests/PrinterHealthMonitorTests.cs`

**Interfaces:**
- Add `PrinterHealthDiagnostic GetDiagnostic(string printerName)` to `IPrinterHealthMonitor`.
- `PrinterHealthDiagnostic` contains `PrinterState` (`Healthy`, `Offline`, `Unavailable`, `Fault`), `IssueKind` (`None`, `PhysicalFault`, `WindowsQueueFault`, `Unknown`), WinSpool status/description, optional WMI code/description, optional Epson popup text, and `IsHealthy`.

- [ ] **Step 1: Add diagnostic classification tests**

Cover healthy status, WMI paper-out/jam/door/ink/service and Epson popup as `PhysicalFault`, and missing/offline/nonfatal queue conditions as `WindowsQueueFault` or `Unavailable`. Assert the existing `HasFatalHardwareError` and printer event tests retain their current results.

- [ ] **Step 2: Run the focused tests and verify the new tests fail**

Run: `dotnet test tests/PrintBit.Tests/PrintBit.Tests.csproj --filter FullyQualifiedName~PrinterHealthMonitorTests`

Expected: FAIL because `GetDiagnostic` and the new result model are absent.

- [ ] **Step 3: Implement the diagnostic using existing WMI, WinSpool, and Epson popup probes**

Centralize the probes so `IsHealthy`, `HasFatalHardwareError`, and `GetDiagnostic` do not disagree. Treat paper, jam, door/cover, ink/toner, service-requested, and matching Epson popup errors as physical. Treat queue missing/offline and Spooler-side statuses as Windows-side conditions. Keep diagnostic reads side-effect free; do not call the existing nudge loop.

- [ ] **Step 4: Run the focused and full tests**

Run: `dotnet test tests/PrintBit.Tests/PrintBit.Tests.csproj --filter FullyQualifiedName~PrinterHealthMonitorTests`

Expected: PASS.

Run: `dotnet test --no-restore`

Expected: PASS with all prior tests unchanged.

- [ ] **Step 5: Commit the diagnostic slice**

```bash
git add src/PrintBit.Infrastructure/Services/PrintService/PrinterHealthDiagnostic.cs src/PrintBit.Infrastructure/Services/PrintService/IPrinterHealthMonitor.cs src/PrintBit.Infrastructure.Windows/PrinterMonitoring/PrinterHealthMonitor.cs tests/PrintBit.Tests/PrinterHealthMonitorTests.cs
git commit -m "feat: expose typed printer health diagnostics"
```

### Task 3: Implement bounded native Spooler recovery

**Files:**
- Create: `src/PrintBit.Shared/Configurations/PrinterRecoverySettings.cs`
- Create: `src/PrintBit.Infrastructure.Windows/PrinterMonitoring/IPrintSpoolerController.cs`
- Create: `src/PrintBit.Infrastructure.Windows/PrinterMonitoring/ServiceControllerSpoolerController.cs`
- Create: `src/PrintBit.Infrastructure.Windows/PrinterMonitoring/PrinterRecoveryService.cs`
- Modify: `src/PrintBit.Infrastructure.Windows/PrintBit.Infrastructure.Windows.csproj`
- Modify: `tests/PrintBit.Tests/PrinterRecoveryServiceTests.cs`

**Interfaces:**
- `IPrintSpoolerController.GetStatusAsync(CancellationToken)` returns `SpoolerStatusSnapshot`.
- `IPrintSpoolerController.RestartAsync(CancellationToken)` returns `SpoolerRestartResult`.
- `PrinterRecoveryService` implements `IPrinterRecoveryService` and consumes `IPrinterHealthMonitor`, `IPrintSpoolerController`, `IPrinterOperationCoordinator`, and `IOptions<PrinterRecoverySettings>`.
- Defaults: `SpoolerTransitionTimeoutSeconds = 30`, `HealthRecheckTimeoutSeconds = 10`, `HealthRecheckIntervalSeconds = 2`; the service name is the fixed Windows service name `Spooler`.

- [ ] **Step 1: Write recovery behavior tests**

Test these exact cases with Moq fakes: healthy status returns `healthy` with `Action=None` and no restart; physical fault returns `manual_intervention_required` with no restart; an offline/Windows queue fault restarts once and returns `recovered` after a healthy recheck; restart exception returns `restart_failed`; an occupied operation lease returns `worker_busy` without querying or restarting.

- [ ] **Step 2: Run focused tests and verify they fail**

Run: `dotnet test tests/PrintBit.Tests/PrintBit.Tests.csproj --filter FullyQualifiedName~PrinterRecoveryServiceTests`

Expected: FAIL because the controller and service are not implemented.

- [ ] **Step 3: Add the stable ServiceController package and implement the controller**

Add `<PackageReference Include="System.ServiceProcess.ServiceController" Version="10.0.8" />` to the Windows project. Refresh the controller before reading status; stop a running Spooler, wait for `Stopped`, start it, and wait for `Running`, using the configured transition timeout and cancellation token. Return operation errors as structured results; never shell out to `cmd.exe` or PowerShell.

- [ ] **Step 4: Implement `PrinterRecoveryService`**

Acquire the recovery lease first. Read the Spooler and printer diagnostic. Return immediately for healthy or physical faults. For Windows-side/unknown faults, perform one restart, then poll read-only diagnostics at the configured interval until healthy or the health deadline. Populate timestamps, action, state, and a user-safe message on every path.

- [ ] **Step 5: Run focused and full tests**

Run: `dotnet test tests/PrintBit.Tests/PrintBit.Tests.csproj --filter FullyQualifiedName~PrinterRecoveryServiceTests`

Expected: PASS.

Run: `dotnet test --no-restore`

Expected: PASS.

- [ ] **Step 6: Commit the native recovery slice**

```bash
git add src/PrintBit.Shared/Configurations/PrinterRecoverySettings.cs src/PrintBit.Infrastructure.Windows/PrinterMonitoring/IPrintSpoolerController.cs src/PrintBit.Infrastructure.Windows/PrinterMonitoring/ServiceControllerSpoolerController.cs src/PrintBit.Infrastructure.Windows/PrinterMonitoring/PrinterRecoveryService.cs src/PrintBit.Infrastructure.Windows/PrintBit.Infrastructure.Windows.csproj tests/PrintBit.Tests/PrinterRecoveryServiceTests.cs
git commit -m "feat: add bounded native spooler recovery"
```

### Task 4: Add the secure worker command pipe

**Files:**
- Create: `src/PrintBit.HardwareService/Services/WorkerCommandPipeHostedService.cs`
- Create: `src/PrintBit.Infrastructure/IPC/WorkerCommandPipeSecurity.cs`
- Create: `src/PrintBit.Infrastructure/IPC/WorkerCommandParser.cs`
- Modify: `src/PrintBit.Shared/Configurations/IpcSettings.cs`
- Create: `tests/PrintBit.Tests/WorkerCommandPipeTests.cs`

**Interfaces:**
- The pipe name is `IpcSettings.WorkerCommandPipeName`, default `printbit-worker-commands`; maximum request size reuses `IpcSettings.MaxMessageBytes`.
- Each connection receives one JSON line, dispatches one command, writes one JSON result line, flushes, and closes.
- The command host consumes `IPrinterRecoveryService`; malformed, oversized, unknown, or missing-command input returns `invalid_request` and never calls recovery.

- [ ] **Step 1: Write parser, dispatch, and ACL tests**

Assert valid command deserialization, explicit enum names, request-ID preservation, malformed/oversized/unknown rejection, one response per connection, and a Windows `PipeSecurity` containing the current service identity plus `BUILTIN\\Administrators` while containing neither `WorldSid` nor `AuthenticatedUserSid`.

- [ ] **Step 2: Run focused tests and verify they fail**

Run: `dotnet test tests/PrintBit.Tests/PrintBit.Tests.csproj --filter FullyQualifiedName~WorkerCommandPipeTests`

Expected: FAIL because the parser, security policy, and hosted service are absent.

- [ ] **Step 3: Implement strict command parsing and secure pipe creation**

Use `NamedPipeServerStreamAcl.Create` with `PipeDirection.InOut`, byte transmission, asynchronous options, one server instance, and a `PipeSecurity` granting full control to the current service identity and read/write to `BuiltinAdministratorsSid` only. Do not call the existing permissive `NamedPipeServerFactory` for this pipe.

- [ ] **Step 4: Implement the hosted request/response loop**

Read UTF-8 lines with a byte-count limit before deserialization. Dispatch only the two enum values, preserve `RequestId`, catch cancellation/disconnect separately from malformed requests, and log command type, request ID, outcome, and elapsed time with `ILogger<WorkerCommandPipeHostedService>`—never payload secrets or `Console.WriteLine`.

- [ ] **Step 5: Run focused and full tests**

Run: `dotnet test tests/PrintBit.Tests/PrintBit.Tests.csproj --filter FullyQualifiedName~WorkerCommandPipeTests`

Expected: PASS.

Run: `dotnet test --no-restore`

Expected: PASS.

- [ ] **Step 6: Commit the command-pipe slice**

```bash
git add src/PrintBit.HardwareService/Services/WorkerCommandPipeHostedService.cs src/PrintBit.Infrastructure/IPC/WorkerCommandPipeSecurity.cs src/PrintBit.Infrastructure/IPC/WorkerCommandParser.cs src/PrintBit.Shared/Configurations/IpcSettings.cs tests/PrintBit.Tests/WorkerCommandPipeTests.cs
git commit -m "feat: add admin-only printer recovery command pipe"
```

### Task 5: Integrate recovery with printing and remove automatic destructive recovery

**Files:**
- Modify: `src/PrintBit.Infrastructure/Services/PrintService/JobOrchestrator.cs`
- Modify: `src/PrintBit.Infrastructure/Services/PrintService/DocumentPrinter.cs`
- Modify: `src/PrintBit.Infrastructure/Services/PrintService/IPrinterHealthMonitor.cs`
- Modify: `src/PrintBit.Infrastructure.Windows/PrinterMonitoring/PrinterHealthMonitor.cs`
- Modify: `tests/PrintBit.Tests/JobOrchestratorTests.cs`
- Modify: `tests/PrintBit.Tests/DocumentPrinterTests.cs`

**Interfaces:**
- `JobOrchestrator` consumes `IPrinterOperationCoordinator` and holds its print lease from entry through terminal event/result construction in a `try/finally`.
- Remove `RecoverAsync(CancellationToken)` from `IPrinterHealthMonitor`; remove `PrinterHealthMonitor.RecoverAsync`, Epson/Sumatra process killing, and the private Spooler shell-out helper.

- [ ] **Step 1: Add integration tests**

Test that a recovery lease makes a new job wait before dispatch, that an active job makes recovery return `worker_busy`, and that a Sumatra timeout returns `PrintFailureStage.Timeout` without invoking any recovery operation. Keep assertions for the existing static `DocumentPrinter` semaphore and terminal cleanup.

- [ ] **Step 2: Run focused tests and verify the new tests fail**

Run: `dotnet test tests/PrintBit.Tests/PrintBit.Tests.csproj --filter "FullyQualifiedName~JobOrchestratorTests|FullyQualifiedName~DocumentPrinterTests"`

Expected: FAIL until the coordinator is injected and the old recovery call is removed.

- [ ] **Step 3: Integrate the coordinator and remove automatic recovery**

Inject `IPrinterOperationCoordinator` into `JobOrchestrator`, acquire the print lease before page counting/events, and dispose it in `finally`. Delete the timeout call to `_healthMonitor.RecoverAsync`; preserve timeout failure reporting, spooler verification, matching-job cleanup, and all existing print settings.

- [ ] **Step 4: Run focused and full tests**

Run: `dotnet test tests/PrintBit.Tests/PrintBit.Tests.csproj --filter "FullyQualifiedName~JobOrchestratorTests|FullyQualifiedName~DocumentPrinterTests"`

Expected: PASS.

Run: `dotnet test --no-restore`

Expected: PASS.

- [ ] **Step 5: Commit the integration slice**

```bash
git add src/PrintBit.Infrastructure/Services/PrintService/JobOrchestrator.cs src/PrintBit.Infrastructure/Services/PrintService/DocumentPrinter.cs src/PrintBit.Infrastructure/Services/PrintService/IPrinterHealthMonitor.cs src/PrintBit.Infrastructure.Windows/PrinterMonitoring/PrinterHealthMonitor.cs tests/PrintBit.Tests/JobOrchestratorTests.cs tests/PrintBit.Tests/DocumentPrinterTests.cs
git commit -m "fix: coordinate printer recovery with print execution"
```

### Task 6: Wire DI/configuration, update the contract docs, and validate the tablet handoff

**Files:**
- Modify: `src/PrintBit.HardwareService/Program.cs`
- Modify: `src/PrintBit.HardwareService/appsettings.json`
- Modify: `src/PrintBit.HardwareService/appsettings.Development.json`
- Modify: `docs/superpowers/specs/printer-outage-alternative.md`
- Modify: `AGENTS.md`
- Create: `tests/PrintBit.Tests/ProgramRegistrationTests.cs`

**Interfaces:**
- Register `PrintOperationCoordinator`, `ServiceControllerSpoolerController`, `PrinterRecoveryService`, and `WorkerCommandPipeHostedService` as singleton/hosted services. The hosted command service must resolve the singleton `IPrinterRecoveryService`.
- Bind `PrinterRecoverySettings` from `PrinterRecoverySettings` and ensure `IpcSettings.WorkerCommandPipeName` is present in both appsettings files.

- [ ] **Step 1: Add DI/configuration tests**

Build the host with test configuration and assert that resolving `IPrinterRecoveryService`, `IPrintSpoolerController`, `IPrinterOperationCoordinator`, and the command hosted service returns singleton-compatible instances. Assert the default command pipe and recovery timeout values bind correctly.

- [ ] **Step 2: Run the registration test and verify it fails**

Run: `dotnet test tests/PrintBit.Tests/PrintBit.Tests.csproj --filter FullyQualifiedName~ProgramRegistrationTests`

Expected: FAIL until registrations and configuration binding are added.

- [ ] **Step 3: Add registrations and settings**

Configure `PrinterRecoverySettings`, register the coordinator and recovery stack before hosted services, and add `AddHostedService<WorkerCommandPipeHostedService>()`. Keep every hardware/recovery component singleton. Update both JSON files with the selected defaults and command pipe name.

- [ ] **Step 4: Update documentation and Node handoff**

Document the command request/response schema, restrictive DACL, LocalSystem/admin identity requirement, result mapping, no-job-operation policy, and Node-owned audit/history in `AGENTS.md` and the outage spec. Include the Node handoff requirement: connect to `printbit-worker-commands` as administrator, use a response deadline longer than the bounded restart, persist the response, and keep transactions blocked until a healthy result.

- [ ] **Step 5: Run the complete verification set**

Run: `dotnet test --no-restore`

Expected: all tests pass.

Run: `dotnet build --no-restore`

Expected: build succeeds; the existing `System.Text.Json` package-pruning warning may remain.

On the target tablet, verify with the installed `PrintBitHardware` service that: admin Node connects to the command pipe; healthy status is a no-op; paper-out returns manual intervention without restart; a Windows-side Spooler fault performs one restart and rechecks; an active print returns busy; and no public kiosk identity can connect.

- [ ] **Step 6: Commit wiring and documentation**

```bash
git add src/PrintBit.HardwareService/Program.cs src/PrintBit.HardwareService/appsettings.json src/PrintBit.HardwareService/appsettings.Development.json docs/superpowers/specs/printer-outage-alternative.md AGENTS.md tests/PrintBit.Tests/ProgramRegistrationTests.cs
git commit -m "docs: wire printer recovery control plane and update contracts"
```

## Self-review checklist

- Every requirement in the outage spec maps to Tasks 1–6; PnP reset, job controls, spool purge, Epson utility launch, and auto-recovery are explicitly deferred.
- All referenced contracts are defined before consumers use them, and every task has a focused failing test, implementation, verification, and commit step.
- No unfinished markers or unspecified fallback behavior remain; health outcomes, timeouts, ACL principals, command fields, and active-print behavior are fixed above.
