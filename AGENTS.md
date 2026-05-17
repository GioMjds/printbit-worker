# AGENTS.md — PrintBit Hardware Service

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

**PrintBit** is a coin-operated self-service printing kiosk deployed on a Windows tablet in campus/institutional environments. This repository (`PrintBit.HardwareService`) is the .NET 10 Windows Service Worker that sits between the physical hardware layer and the kiosk UI.

### What this service is NOT

- It is **not a web API**. There are no HTTP controllers, no REST endpoints, no request/response cycles in the traditional sense.
- It is **not a stateless service**. `TransactionStateMachine` is a singleton with mutable in-memory state that reflects real physical hardware state.
- It is **not safe to restart mid-transaction**. A restart during `Printing` or `DispensingChange` will lose state and potentially leave the hardware in an undefined condition.

### What this service IS

- A **hardware bridge**: reads serial data from an ESP32 microcontroller, interprets it as domain events, drives a state machine, and dispatches print jobs to a physical printer.
- A **Windows Service**: runs under `Microsoft.Extensions.Hosting.WindowsServices`, responds to SCM lifecycle signals. Startup/shutdown order matters.
- A **real-time event processor**: coin pulses arrive asynchronously from hardware. The `Channel<Esp32Message>` queue decouples the serial ISR-equivalent from business logic. Do not collapse this into a synchronous call chain.

### Why normal assumptions break here

| Assumption | Reality in PrintBit |
|---|---|
| Services can be scoped per-request | Everything hardware-related is `Singleton`. Scoped/Transient lifetimes will cause DI errors or lost state. |
| I/O failures are retryable | Serial disconnection is not auto-recovered. Coin pulses missed are gone. |
| Timeouts can be tuned freely | The 2-minute print timeout exists because SumatraPDF cold-starts on Windows can be slow. Do not reduce it. |
| State lives in a database | Transaction state is in-memory only. There is no persistence layer for live state. |
| Logging is optional | Every state transition, coin event, and print result must be logged. Hardware debugging has no debugger. |

---

## 2. Hardware Architecture Brief

### Physical Wiring Overview

```
[Coin Acceptor]
  └─ pulse signal on GPIO pin → ESP32
        └─ ESP32 counts pulses, maps to denomination
        └─ sends "COIN:<value>" over UART → USB Serial → Windows Tablet

[Hopper (coin change dispenser)]
  └─ GPIO pin controlled by ESP32
        └─ ESP32 receives "HOPPER_DISPENSE" command over UART ← Windows Tablet
        └─ pulses the hopper motor, counts dispensed coins
        └─ sends "HOPPER:DONE" when complete

[Epson L5290 Series Printer]
  └─ connected via USB to Windows Tablet
        └─ registered in Windows as "EPSON L5290 Series"
        └─ dispatched via SumatraPDF.exe process (NOT via WIA, NOT via network, NOT via raw socket)
        └─ PrintService spawns SumatraPDF.exe as a child process with -print-to flag
```

### ESP32 Role

The ESP32 is the hardware abstraction layer. It:
- Debounces coin acceptor pulses (do NOT implement pulse deduplication in C# — the ESP32 already handles this)
- Maps pulse counts to coin denominations before sending over serial
- Drives the hopper GPIO on command
- Sends periodic `PING` keepalives

The ESP32 does **not** know about print jobs, file paths, or pricing. That logic lives entirely in `TransactionStateMachine`.

### Serial Connection

| Property | Value |
|---|---|
| Default port | `COM3` (configurable via `HardwareSettings.Esp32Port`) |
| Baud rate | `115200` (configurable via `HardwareSettings.Esp32BaudRate`) |
| Protocol | Line-delimited ASCII (`SerialPort.ReadLine()` / `WriteLine()`) |
| Framing | No checksum, no ACK. Messages are fire-and-forget from ESP32. |

### Inbound Messages (ESP32 → Service)

| Raw Message | `Esp32MessageType` | `Value` | Meaning |
|---|---|---|---|
| `COIN:<int>` | `CoinInserted` | denomination integer | Coin inserted; value in kiosk currency units |
| `HOPPER:DONE` | `HopperCompleted` | null | Hopper finished dispensing change |
| `PING` | `Heartbeat` | null | ESP32 keepalive; respond with `PONG` |
| _(anything else)_ | `Unknown` | null | Logged and discarded |

### Outbound Commands (Service → ESP32)

Defined in `Esp32Command` (static constants — do not hardcode these strings elsewhere):

| Constant | Value | When to send |
|---|---|---|
| `Esp32Command.Ping` | `"PONG"` | On `Heartbeat` received |
| `Esp32Command.HopperDispense` | `"HOPPER_DISPENSE"` | When change must be dispensed post-print |
| `Esp32Command.PrinterStart` | `"PRINTER_START"` | When print job begins |
| `Esp32Command.PrinterComplete` | `"PRINTER_COMPLETE"` | When print job finishes successfully |

### Epson L5290 Print Dispatch

`PrintService` dispatches print jobs by spawning `SumatraPDF.exe` as a child process:

```
SumatraPDF.exe -print-to "EPSON L5290 Series" -print-settings "<copies>" "<filePath>"
```

**Critical constraints:**
- `SumatraPDF.exe` must be on `PATH` or in the working directory. The service does not manage its location.
- The printer name string `"EPSON L5290 Series"` must match exactly what Windows registers. A mismatch silently fails.
- Print jobs are serialized via `SemaphoreSlim(1, 1)` — one job at a time. Do not remove or work around this lock.
- Timeout is **2 minutes** via linked `CancellationTokenSource`. SumatraPDF cold-starts on Windows tablets can take 15–30 seconds before spooling begins. Do not reduce this timeout.
- Exit code `!= 0` is treated as failure. `PrintJobResult.Success = false` is returned and the state machine calls `Reset()`.

---

## 3. Codebase Orientation

### Solution Structure

```
PrintBit.HardwareService   ← Worker Service host, DI root, entry point
PrintBit.Application       ← Domain logic: state machine, orchestration, handlers, queue
PrintBit.Hardware          ← Hardware abstractions: ESP32 device, message types, commands
PrintBit.Infrastructure    ← I/O implementations: serial, print, watchdog, IPC stubs
PrintBit.Shared            ← Cross-cutting: enums, config models, DTOs
```

**Dependency direction:** `HardwareService` → `Application` → `Hardware` / `Infrastructure` → `Shared`
Never reverse this. `Shared` has zero project references. `Infrastructure` does not reference `Application`.

### Key Classes & Responsibilities

| Class | Project | Responsibility |
|---|---|---|
| `Worker` | HardwareService | Connects ESP32 on startup; forwards `MessageReceived` events into `HardwareEventQueue` |
| `HardwareProcessingService` | HardwareService | `BackgroundService` that drains `HardwareEventQueue` and calls `HardwareOrchestrator` |
| `HardwareOrchestrator` | Application | Routes `Esp32Message` by type to the correct handler |
| `CoinInsertedHandler` | Application | Calls `TransactionStateMachine.InsertCoin()` |
| `StartPrintHandler` | Application | Drives state machine through `StartPrinting()` → `PrintAsync()` → `Complete()` or `Reset()` |
| `TransactionStateMachine` | Application | Single source of truth for transaction state and balance |
| `HardwareEventQueue` | Application | Bounded `Channel<Esp32Message>` (capacity 1024, single-reader) |
| `Esp32Device` | Hardware | Wraps serial connection; parses raw strings into typed `Esp32Message` |
| `SerialConnection` | Infrastructure | Wraps `System.IO.Ports.SerialPort`; raises `DataReceived` event |
| `PrintService` | Infrastructure | Spawns SumatraPDF child process; owns the print semaphore |
| `WatchdogService` | Infrastructure | Heartbeat logger; wired for future hardware health checks |

### DI Registration (Program.cs)

All hardware-related services are registered as **Singleton**. This is intentional and must not be changed without understanding the statefulness implications.

```csharp
// Current registrations — keep in sync with actual Program.cs
builder.Services.AddSingleton<ISerialConnection, SerialConnection>();
builder.Services.AddSingleton<IEsp32Device, Esp32Device>();
builder.Services.AddSingleton<IPrintService, PrintService>();
builder.Services.AddSingleton<TransactionStateMachine>();
builder.Services.AddSingleton<CoinInsertedHandler>();
builder.Services.AddSingleton<StartPrintHandler>();
builder.Services.AddSingleton<HardwareOrchestrator>();
builder.Services.AddSingleton<HardwareEventQueue>();
builder.Services.AddSingleton<WatchdogService>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<HardwareProcessingService>();
```

### Configuration

Bound from `appsettings.json` via `IOptions<HardwareSettings>`:

```json
{
  "HardwareSettings": {
    "Esp32Port": "COM3",
    "Esp32BaudRate": 115200,
    "WatchdogIntervalSeconds": 5
  }
}
```

Override for development in `appsettings.Development.json`. Never hardcode port names or baud rates in source files — always read from `HardwareSettings`.

### Queue Design

`HardwareEventQueue` wraps `Channel<Esp32Message>` with:
- `BoundedChannelOptions(capacity: 1024)` — backpressure if processing falls behind
- `SingleReader = true` — only `HardwareProcessingService` reads
- `SingleWriter = false` — multiple serial events can enqueue concurrently
- `FullMode = BoundedChannelFullMode.Wait` — blocks the writer instead of dropping messages

Do not change `SingleReader = true` or add a second consumer without redesigning the processing pipeline.

---

## 4. State Machine Contract

`TransactionStateMachine` is the authoritative state authority. No class may mutate transaction state except through its public methods.

### State Diagram

```
                    ┌─────────────────────────────────────────────┐
                    │                                             │
                    ▼                                             │ Reset()
                  Idle ──[InsertCoin()]──► WaitingForCoins        │ (from any state on failure)
                                              │                   │
                                    balance >= ₱5                 │
                                              │                   │
                                              ▼                   │
                                        ReadyToPrint              │
                                              │                   │
                                    StartPrinting()               │
                                              │                   │
                                              ▼                   │
                                          Printing ──────────────►│
                                              │         failure   │
                                        Complete()                │
                                              │                   │
                                              ▼                   │
                                         Completed ──────────────►┘
                                                       Reset()
```

### Transition Rules

| Method | Valid From | Transitions To | Side Effects |
|---|---|---|---|
| `InsertCoin(amount)` | `Idle`, `WaitingForCoins` | `WaitingForCoins` or `ReadyToPrint` | Accumulates `CurrentBalance` |
| `StartPrinting()` | `ReadyToPrint` only | `Printing` | None |
| `Complete()` | `Printing` only | `Completed` | None |
| `Reset()` | Any state | `Idle` | Sets `CurrentBalance = 0` |

**Never call `StartPrinting()` unless `CurrentState == ReadyToPrint`.** The orchestrator checks this before delegating to `StartPrintHandler`. Do not bypass this check.

**Always call `Reset()` on failure.** `StartPrintHandler` catches all exceptions and calls `Reset()`. Any new handler that drives the print lifecycle must do the same.

### Payment Threshold

The threshold `balance >= 5` (₱5) is hardcoded in `TransactionStateMachine.InsertCoin()`. This is intentional for the current kiosk pricing model. Do not parameterize this without an explicit task to do so — it has downstream implications for the UI flow and pricing configuration.

### States Currently Unused

- `DispensingChange` — defined in the enum, not yet wired into the state machine
- `Error` — defined in the enum, not yet wired; `Reset()` is used instead

Do not wire these states without a corresponding task. Do not remove them from the enum.

---

## 5. Agent Behavioral Rules

### ✅ Agent MAY

- Add new `case` branches to `HardwareOrchestrator.HandleEsp32MessageAsync()` for new `Esp32MessageType` values
- Add new values to `Esp32MessageType` enum (update `Esp32Device.ParseMessage()` and `AGENTS.md` protocol table)
- Add new constants to `Esp32Command` (update `AGENTS.md` outbound commands table)
- Implement or extend active, non-stub classes following existing patterns
- Write unit tests for `TransactionStateMachine`, `Esp32Device.ParseMessage()`, and handler logic
- Add XML doc comments to public interfaces and classes
- Add `IOptions<T>` configuration models to `PrintBit.Shared` for new settings
- Extend `HardwareSettings` with new config keys (update `AGENTS.md` configuration section)
- Refactor private internals that do not change public interfaces or DI contracts

### ❌ Agent MUST NOT

- Change the public API of `TransactionStateMachine` (method signatures, state enum values in use) without an explicit task
- Remove or bypass `SemaphoreSlim(1, 1)` in `PrintService`
- Reduce the 2-minute print timeout
- Hardcode serial port names, baud rates, printer names, or file paths in source files
- Register any hardware service as `Scoped` or `Transient` in DI
- Add `Console.WriteLine` anywhere — use `ILogger<T>` exclusively
- Use `new` to instantiate services — always inject via constructor
- Reverse the dependency direction between projects
- Add direct references from `Infrastructure` to `Application`
- Implement pulse deduplication or coin debouncing in C# — this is the ESP32's responsibility
- Modify `HardwareEventQueue` channel options without understanding backpressure implications

### 🟡 Stub Policy — Propose Only

The following files are stubs reserved for future implementation. The agent **must not write compilable implementation code** into them. Instead, leave a structured proposal comment:

**Stub files:**
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

**Proposal comment format:**

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

### Coin Acceptor

**Debouncing is handled by the ESP32.** The service receives already-processed `COIN:<int>` messages. Do not add debounce timers, deduplication logic, or pulse counting in C#.

**Coin value is an integer.** The ESP32 maps pulse counts to integer denomination units before sending. `Esp32Message.Value` is `int?`, not `decimal`. The `CoinInsertedHandler` casts to `decimal` for balance accumulation — this is intentional for precision in the state machine.

**Rapid coin insertion is valid.** A user may insert multiple coins quickly. The `Channel` queue handles burst; `TransactionStateMachine.InsertCoin()` accumulates correctly. Do not add rate limiting on coin events.

### Hopper

**`HOPPER:DONE` may arrive after a reconnect.** The hopper sends a completion signal when finished, but if the serial connection drops and reconnects mid-dispense, the signal may arrive late or be missed entirely. The handler for `HopperCompleted` (when implemented) must be idempotent.

**Do not send `HOPPER_DISPENSE` more than once per transaction.** There is no hardware-level guard against double-dispensing. The state machine's `DispensingChange` state (when wired) is the guard.

### Serial Connection

**Reconnection is not implemented.** `SerialConnection` connects once on `Worker.ExecuteAsync()` startup. If the serial port disconnects (USB unplug, device reset), the service will stop receiving events but will not attempt to reconnect. Do not assume reconnection logic exists. Do not implement it without an explicit task.

**`ReadLine()` is blocking on the serial thread.** `SerialPort.DataReceived` fires on a thread pool thread. The event chain (`OnDataReceived` → `DataReceived` event → `Esp32Device.OnDataReceived` → `HardwareEventQueue.EnqueueAsync`) must remain non-blocking except for the `ValueTask` await on the channel write.

### Print Pipeline

**SumatraPDF cold-start is slow.** On a Windows tablet with limited resources, the first SumatraPDF invocation after boot may take 15–30 seconds before the print job reaches the spooler. The 2-minute timeout accounts for this. Do not reduce it.

**The printer name is exact-match.** Windows print spooler matches on the exact string `"EPSON L5290 Series"`. A trailing space, different casing, or locale variant will cause a silent failure (SumatraPDF exits 0 but nothing prints). If the printer name needs to be configurable, add it to `HardwareSettings` and update `AGENTS.md`.

**Only one print job runs at a time.** `SemaphoreSlim(1, 1)` in `PrintService` is a hard serialization point. This is correct — the physical printer is a single device. Do not parallelize or pipeline print jobs.

**File path is currently hardcoded.** `HardwareOrchestrator` passes `@"C:\PrintBit\sample.pdf"` to `StartPrintHandler`. This is a known gap. When the file path wiring from the kiosk UI is implemented, it must come through `StartPrintEvent.FilePath` — the handler is already structured for this.

### State Machine

**There is no persistence.** If the service restarts during `Printing`, the transaction is lost. The physical printer may or may not complete the job. The hardware returns to `Idle` on service restart. This is a known limitation — do not add ad-hoc file-based state persistence without a proper task.

**`DispensingChange` and `Error` states are defined but not wired.** Adding transitions to these states requires updating both `TransactionStateMachine` and `HardwareOrchestrator`. Do not wire them partially.

---

## 7. Self-Maintenance Rules

The agent is responsible for keeping `AGENTS.md` accurate. **Before closing any task**, diff the changes made against what `AGENTS.md` documents. If anything is stale, update this file in the same commit.

### Triggers → Required Updates

| What changed | Section(s) to update |
|---|---|
| Added/renamed/removed constant in `Esp32Command` | §2 Outbound Commands table |
| Added/changed `Esp32MessageType` enum value | §2 Inbound Messages table, §3 Key Classes |
| Added/changed `HardwareSettings` property | §3 Configuration block |
| Added/removed DI registration in `Program.cs` | §3 DI Registration block |
| Changed `TransactionState` enum or state machine transitions | §4 State Diagram + Transition Rules table |
| Added a new package (`<PackageReference>`) to any `.csproj` | §3 Codebase Orientation (note the dependency) |
| Promoted a stub to active implementation | §5 Stub Policy list (remove from list) |
| Added a new stub file | §5 Stub Policy list (add to list) |
| Changed print timeout, semaphore, or SumatraPDF arguments | §2 Epson L5290 section + §6 Print Pipeline |
| Changed serial port defaults or framing | §2 Serial Connection table |
| Added/changed environment variable or `appsettings` key | §3 Configuration block |
| Added tests that document previously undocumented behavior | §5 Agent MAY (note what's now tested) |
| Changed public interface of any class in §3 Key Classes table | §3 Key Classes table |

### Update Format

- Keep tables in sync — do not leave stale rows
- Keep code blocks in sync with actual source (DI registrations, config JSON, command strings)
- Do not add editorial commentary — keep entries factual and terse
- If a section becomes significantly out of date and requires a full rewrite, rewrite it — do not patch over contradictions

### Commit Message Convention

When updating `AGENTS.md` as part of a task:

```
docs: update AGENTS.md — <what changed>

e.g.
docs: update AGENTS.md — add HOPPER_PULSE to inbound commands table
docs: update AGENTS.md — promote HopperDevice from stub to active
docs: update AGENTS.md — add PrinterName to HardwareSettings config
```