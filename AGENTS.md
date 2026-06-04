# AGENTS.md - PrintBit Hardware Service

> This file is the authoritative brief for any AI coding agent working in this repository.
> Read it in full before making any change. Update it before closing any task that modifies
> what this file documents. See [Section 7: Self-Maintenance Rules](#7-self-maintenance-rules).

---

## Table of Contents

1. [Project Identity & Domain Context](#1-project-identity--domain-context)
2. [Hardware Architecture Brief](#2-hardware-architecture-brief)
3. [Codebase Orientation](#3-codebase-orientation)
4. [State Machine Contract](#4-state-machine-contract)
5. [Agent Behavioral Rules](#5-agent-behavioral-rules)
6. [Edge Cases & Hardware Constraints](#6-edge-cases--hardware-constraints)
7. [Self-Maintenance Rules](#7-self-maintenance-rules)

---

## 1. Project Identity & Domain Context

**PrintBit** is a coin-operated self-service printing kiosk deployed on a Windows tablet in campus/institutional environments. This repository (`PrintBit.HardwareService`) is the .NET 10 Windows Service Worker focused on printer spooler orchestration and logging error messages from the Node.js app via named pipe.

### What this service is NOT

- It is **not a web API**. There are no HTTP controllers and no REST endpoints.
- It does **not** handle ESP32 coin/hopper hardware at runtime (printer-only mode).

### What this service IS

- A **printer bridge**: watches a print queue and dispatches print jobs through the Windows spooler.
- A **Windows Service**: startup/shutdown sequencing matters.
- An **IPC sink**: logs error messages sent from the Node.js app over a named pipe.
- An **IPC source**: emits print lifecycle events to the Node.js app over a named pipe.

### Why normal assumptions break here

| Assumption | Reality in PrintBit |
|---|---|
| Services can be scoped per request | Printer services are singleton; scoped/transient lifetimes can break job serialization. |
| I/O failures are retryable | Spooler failures are not auto-recovered; log and investigate. |
| Timeout can be tuned freely | 2-minute print timeout exists for slow Sumatra cold starts on tablets. |
| State lives in a database | Print pipeline state is in-memory only. |
| Logging is optional | Printer debugging depends on logs for print outcomes and Node error payloads. |

---

## 2. Hardware Architecture Brief

ESP32/coin/hopper paths are currently **not wired in the printer-only runtime**. The details below are retained for legacy context.

### Physical Wiring Overview

```
[Coin Acceptor]
  -> pulse signal on GPIO pin -> ESP32
        -> ESP32 maps pulse counts to denomination
        -> sends "COIN:<value>" via UART -> USB serial -> tablet

[Hopper]
  -> GPIO controlled by ESP32
        -> ESP32 receives "HOPPER_DISPENSE" from service
        -> sends "HOPPER:DONE" when dispense completes

[Epson L5290 Printer]
  -> USB to tablet
        -> dispatched via SumatraPDF.exe process
```

### ESP32 Role

ESP32 handles:
- Coin pulse debouncing (do not duplicate this in C#)
- Pulse-to-denomination mapping
- Hopper GPIO control
- `PING` keepalive messages

ESP32 does not handle pricing logic, file paths, or print lifecycle semantics.

### Serial Connection

| Property | Value |
|---|---|
| Default port | `COM3` (`HardwareSettings.Esp32Port`) |
| Baud rate | `115200` (`HardwareSettings.Esp32BaudRate`) |
| Protocol | Line-delimited ASCII (`ReadLine()` / `WriteLine()`) |
| Framing | No checksum, no ACK |

### Inbound Messages (ESP32 -> Service)

| Raw Message | `Esp32MessageType` | `Value` | Meaning |
|---|---|---|---|
| `COIN:<int>` | `CoinInserted` | denomination integer | Coin inserted |
| `HOPPER:DONE` | `HopperCompleted` | null | Hopper completed |
| `PING` | `Heartbeat` | null | ESP32 keepalive |
| anything else | `Unknown` | null | Logged and ignored |

### Outbound Commands (Service -> ESP32)

Defined in `Esp32Command`:

| Constant | Value | When to send |
|---|---|---|
| `Esp32Command.Ping` | `"PONG"` | On heartbeat receive |
| `Esp32Command.HopperDispense` | `"HOPPER_DISPENSE"` | When dispensing change |
| `Esp32Command.PrinterStart` | `"PRINTER_START"` | When print begins |
| `Esp32Command.PrinterComplete` | `"PRINTER_COMPLETE"` | On verified print success |

### Epson L5290 Print Dispatch

`PrintService` runs:

```
SumatraPDF.exe -print-to "<printerName>" -print-settings "<copies>" -silent "<filePath>"
```

Critical constraints:
- `SumatraPDF.exe` path is currently `C:\Users\printbit\bin\SumatraPDF.exe`.
- Printer name comes from `HardwareSettings.PrinterName` and must exactly match Windows registration.
- Print jobs are serialized via `SemaphoreSlim(1, 1)`.
- Timeout is 2 minutes (`HardwareSettings.PrintTimeoutSeconds = 120`).
- Exit code `0` is not enough: service also verifies spooler lifecycle (`Win32_PrintJob`) before returning success.
- Any process or verification failure returns `Success = false` with stage detail.

### Named Pipe (Node Error Intake)

`ErrorPipeHostedService` listens on a named pipe and logs **line-delimited JSON** error messages from the Node.js app.

| Field | Required | Notes |
|---|---|---|
| `message` | Yes | Error message to log |
| `code` | No | Optional error code |
| `source` | No | Source identifier (e.g., kiosk UI) |
| `transactionId` | No | Optional transaction identifier for correlation |
| `spoolerCorrelationKey` | No | Optional spooler correlation key |
| `stack` | No | Optional stack trace |
| `timestampUtc` | No | ISO-8601 timestamp |

### Named Pipe (Service -> Node Return Pipe)

`WorkerEventPipeClient` connects to the Node return pipe and sends **line-delimited JSON** print events.

| Field | Required | Notes |
|---|---|---|
| `type` | Yes | `PrintStarted`, `PrintSucceeded`, or `PrintFailed` |
| `transactionId` | No | Parsed from queue file name |
| `spoolerCorrelationKey` | No | Parsed from queue file name |
| `fileName` | No | Queue file name |
| `printerName` | No | Printer display name |
| `failureStage` | No | `PrintFailureStage` string when failed |
| `message` | No | Success summary or failure detail |
| `timestampUtc` | Yes | ISO-8601 timestamp |

---

## 3. Codebase Orientation

### Solution Structure

```
PrintBit.HardwareService   <- Host and background services
PrintBit.Application       <- State machine, handlers, orchestration, queue
PrintBit.Hardware          <- ESP32 abstractions and message parsing
PrintBit.Infrastructure    <- Serial, print, IPC, watchdog implementations
PrintBit.Shared            <- Enums and configuration models
```

Dependency direction:
`HardwareService` -> `Application` -> `Hardware` / `Infrastructure` -> `Shared`

### Key Classes & Responsibilities

| Class | Project | Responsibility |
|---|---|---|
| `PrintQueueWatcherService` | HardwareService | Watches queue directory, invokes `IPrintService`, and emits worker events |
| `ErrorPipeHostedService` | HardwareService | Reads Node.js error messages from named pipe and logs them |
| `PrinterMonitorService` | Infrastructure.Windows | Logs printer status and job info from Windows spooler |
| `PrintService` | Infrastructure | Sumatra process + spooler verification + print lock |
| `WorkerEventPipeClient` | Infrastructure | Sends print lifecycle events to Node via return pipe |

Legacy ESP32/orchestrator classes remain in the codebase but are not wired in the printer-only runtime.

### DI Registration (Program.cs)

All hardware services are singleton.

```csharp
builder.Services.Configure<HardwareSettings>(
    builder.Configuration.GetSection("HardwareSettings"));
builder.Services.Configure<IpcSettings>(
    builder.Configuration.GetSection("IpcSettings"));

builder.Services.AddHostedService<PrintQueueWatcherService>();
builder.Services.AddHostedService<ErrorPipeHostedService>();
builder.Services.AddHostedService<PrinterMonitorService>();

builder.Services.AddSingleton<IPrintService, PrintService>();
builder.Services.AddSingleton<IPrintRecoveryService, PrintRecoveryService>();
builder.Services.AddSingleton<WorkerEventPipeClient>();
```

### Configuration

Bound from `appsettings.json` via `IOptions<HardwareSettings>`:

```json
{
  "HardwareSettings": {
    "Esp32Port": "COM3",
    "Esp32BaudRate": 115200,
    "WatchdogIntervalSeconds": 5,
    "PrintTimeoutSeconds": 120,
    "PrinterName": "EPSON L5290 Series",
    "PrintQueueDirectory": "C:\\Users\\printbit\\printbit-worker\\queue"
  },
  "IpcSettings": {
    "PipeName": "printbit-node-errors",
    "MaxMessageBytes": 8192,
    "WorkerReturnPipeName": "printbit-worker-events"
  }
}
```

### Queue Design

`HardwareEventQueue` remains in the codebase for the legacy ESP32 path but is not used by the printer-only runtime.

---

## 4. State Machine Contract

`TransactionStateMachine` is the single source of truth for transaction state.

Printer-only runtime note: the transaction state machine is not wired in the current service host.

### State Diagram

```
Idle -> Pending -> ReadyToPrint -> Printing -> Verifying -> Success
  \                                               \
   \-> (failure from non-terminal state) ---------> Failed

Reset() from any state -> Idle
```

### Transition Rules

| Method | Valid From | Transitions To | Side Effects |
|---|---|---|---|
| `TryInsertCoin(amount)` | `Idle`, `Pending` | `Pending` or `ReadyToPrint` | Adds `CurrentBalance` |
| `TryStartPrinting()` | `ReadyToPrint` | `Printing` | Clears failure reason |
| `TryStartVerifying()` | `Printing` | `Verifying` | None |
| `TryMarkSuccess()` | `Verifying` | `Success` | Clears failure reason |
| `TryMarkFailed(reason)` | any non-terminal state | `Failed` | Sets `LastFailureReason` |
| `Reset()` | any state | `Idle` | Clears balance and failure reason |

### Lifecycle Constraints

- Start print only when `CurrentState == ReadyToPrint`.
- `HardwareOrchestrator` is the gate for all starts (ESP32 path and queue watcher path).
- Failed transactions remain in `Failed` until explicit reset (`PipeMessageType.ResetTransactionRequest`).
- Invalid transitions are rejected and logged.

### Payment Threshold

`balance >= 5` is hardcoded in `TryInsertCoin()`. Do not parameterize without explicit task.

---

## 5. Agent Behavioral Rules

### Agent MAY

- Extend `HardwareOrchestrator` routing for new message types.
- Add values to `Esp32MessageType` and update parser/docs.
- Add `Esp32Command` constants and update docs.
- Extend active non-stub classes.
- Add/extend tests for `TransactionStateMachine`, handlers, orchestrator gating, IPC reset, and print verification behavior.
- Add new `IOptions<T>` config models in `PrintBit.Shared`.
- Extend `HardwareSettings` and sync docs.

### Agent MUST NOT

- Remove or bypass `SemaphoreSlim(1, 1)` in `PrintService`.
- Reduce the 2-minute print timeout.
- Hardcode serial settings, printer name, or paths when config is available.
- Register hardware services as scoped/transient.
- Use `Console.WriteLine`; use `ILogger<T>`.
- Reverse project dependency direction.
- Add `Infrastructure -> Application` references.
- Implement coin dedup/debounce logic in C#.
- Change channel options without explicit backpressure analysis.

### Stub Policy - Propose Only

Do not implement the following files with compilable code. Use structured proposal comments only.

Stub files:
- `PrintBit.Hardware/Devices/CoinAcceptor/CoinAcceptorDevice.cs`
- `PrintBit.Hardware/Devices/CoinAcceptor/ICoinAcceptor.cs`
- `PrintBit.Hardware/Devices/CoinAcceptor/CoinPulseEvent.cs`
- `PrintBit.Hardware/Devices/Hopper/HopperDevice.cs`
- `PrintBit.Hardware/Devices/Hopper/IHopper.cs`
- `PrintBit.Hardware/Devices/Printer/EpsonPrinterDevice.cs`
- `PrintBit.Hardware/Devices/Printer/IPrinterDevice.cs`
- `PrintBit.Hardware/StateMachine/HardwareStateMachine.cs`
- `PrintBit.Hardware/StateMachine/PrintJobStateMachine.cs`
- `PrintBit.Infrastructure/Services/IPC/NamedPipeServer.cs`
- `PrintBit.Infrastructure/Services/IPC/SocketServer.cs`
- `PrintBit.Infrastructure/Services/IPC/MessageDispatcher.cs`
- `PrintBit.Infrastructure/Services/TransactionService/TransactionService.cs`
- `PrintBit.Infrastructure/Services/PrintService/GhostScriptRunner.cs`
- `PrintBit.Shared/Models/TransactionDto.cs`
- `PrintBit.Shared/Models/HardwareStatusDto.cs`
- `PrintBit.Shared/Models/PrinterStatusDto.cs`
- `PrintBit.Shared/Models/Esp32MessageDto.cs`
- `PrintBit.Shared/Enums/HardwareState.cs`
- `PrintBit.Shared/Enums/PrinterState.cs`
- `PrintBit.Shared/Constants/HardwareConstants.cs`
- `PrintBit.Application/Handlers/HopperDispenseHandler.cs`
- `PrintBit.Application/Handlers/PrintCompletedHandler.cs`
- `PrintBit.Application/Events/HardwareHeartbeatEvent.cs`

Proposal comment format:

```csharp
// AGENT PROPOSAL:
// Interface suggestion:
//   Task DispenseAsync(int coins, CancellationToken ct = default);
//   event EventHandler<HopperCompletedEvent>? DispenseCompleted;
//
// Implementation notes:
//   - Send Esp32Command.HopperDispense via IEsp32Device.SendCommand()
//   - Await HopperCompleted message via HardwareEventQueue (or dedicated channel)
//   - Register as Singleton in Program.cs
//   - Wire into HardwareOrchestrator after CoinInserted path
//
// Depends on: IEsp32Device, HardwareEventQueue
// Blocks: DispensingChange state in TransactionStateMachine
```

---

## 6. Edge Cases & Hardware Constraints

ESP32/coin/hopper constraints below are legacy context and not used in the current printer-only runtime.

### Coin Acceptor

- Debounce is done by ESP32.
- Coin value is integer (`Esp32Message.Value`), converted to decimal in state machine path.
- Rapid insertion is valid; queue burst handling is intentional.

### Hopper

- `HOPPER:DONE` can be delayed or missed across reconnect scenarios.
- Hopper completion handling must be idempotent when implemented.

### Serial

- Reconnection is not implemented.
- `ReadLine()` is blocking on serial callback thread; keep event path lightweight.

### Print Pipeline

- Sumatra cold-start may take 15-30 seconds.
- Exact printer-name matching is required.
- Print execution is single-job serialized (`SemaphoreSlim(1, 1)`).
- Success requires both process success and spooler lifecycle verification.
- Queue watcher print requests go directly to `PrintService` (no transaction gate).

### State Machine

- No persistence across service restart.
- Failed transactions stay in `Failed` until explicit named-pipe reset.

---

## 7. Self-Maintenance Rules

Before closing any task, diff what changed against this file and update stale sections.

### Triggers -> Required Updates

| What changed | Section(s) to update |
|---|---|
| `Esp32Command` constants changed | Section 2 outbound commands |
| `Esp32MessageType` changed | Section 2 inbound messages, Section 3 key classes |
| `HardwareSettings` changed | Section 3 configuration |
| DI registration changed | Section 3 DI registration |
| `TransactionState` or transitions changed | Section 4 |
| New package added to a `.csproj` | Section 3 orientation/dependencies |
| Stub promoted to implementation | Section 5 stub list |
| New stub added | Section 5 stub list |
| Print timeout/semaphore/arguments changed | Sections 2 and 6 |
| Serial defaults/framing changed | Section 2 serial connection |
| `appsettings` keys changed | Section 3 configuration |
| New tests documenting behavior | Section 5 Agent MAY |
| Public interface in Section 3 changed | Section 3 key classes |

### Update Format

- Keep tables and code blocks aligned with source.
- Keep statements factual and terse.
- Rewrite sections when needed; do not leave contradictions.

### Commit Message Convention

When updating this file:

```
docs: update AGENTS.md - <what changed>
```
