# PrintBit Hardware Service

A .NET 10 Windows Service Worker focused on the printer spooler and print queue for the PrintBit kiosk system. It also logs error messages received from the Node.js app over a named pipe.

---

## Architecture

```
PrintBit.HardwareService    ← Worker Service host (entry point)
├── PrintBit.Application    ← State machine, orchestration, event handlers
├── PrintBit.Hardware       ← ESP32 device abstraction, message parsing
├── PrintBit.Infrastructure ← Serial comms, print dispatch, watchdog
└── PrintBit.Shared         ← Enums, DTOs, configuration models
```

### Request Flow

```
Print queue → PrintQueueWatcherService → PrintService (SumatraPDF + spooler verify)
Node.js errors → ErrorPipeHostedService (named pipe) → ILogger
```

---

## Projects

### `PrintBit.HardwareService`
Worker Service host. Runs printer-only background services.

| Service | Role |
|---|---|
| `PrintQueueWatcherService` | Watches the queue directory and submits print jobs |
| `ErrorPipeHostedService` | Reads Node.js error messages from a named pipe and logs them |
| `PrinterMonitorService` | Logs printer status and job state from Windows spooler |

### `PrintBit.Application`
Business logic layer (present but not wired in the printer-only runtime). No direct I/O dependencies.

| Class | Role |
|---|---|
| `TransactionStateMachine` | Tracks `TransactionState` (Idle → WaitingForCoins → ReadyToPrint → Printing → Completed) and `CurrentBalance` |
| `HardwareOrchestrator` | Routes `Esp32Message` types to the correct handler |
| `CoinInsertedHandler` | Delegates coin events to `TransactionStateMachine.InsertCoin()` |
| `StartPrintHandler` | Drives state machine through print lifecycle; calls `IPrintService` |
| `HardwareEventQueue` | Bounded `Channel<Esp32Message>` (1024 capacity, single-reader) |

### `PrintBit.Hardware`
Hardware abstraction layer (not wired in the printer-only runtime).

| Class | Role |
|---|---|
| `Esp32Device` | Wraps `ISerialConnection`; parses raw serial strings into typed `Esp32Message` |
| `Esp32Message` | Typed message: `Type`, `Value`, `Raw`, `TimestampUtc` |
| `Esp32MessageType` | `CoinInserted`, `HopperCompleted`, `Heartbeat`, `Unknown`, etc. |
| `Esp32Command` | Static command strings sent back to ESP32 (`HOPPER_DISPENSE`, `PONG`, etc.) |

### `PrintBit.Infrastructure`
I/O services (print process, printer monitoring, IPC helpers).

| Class | Role |
|---|---|
| `SerialConnection` | Wraps `System.IO.Ports.SerialPort`; exposes `DataReceived` event |
| `PrintService` | Spawns `SumatraPDF.exe` process with `-print-to`; uses `SemaphoreSlim(1,1)` to serialize jobs; 2-minute timeout |
| `WatchdogService` | Heartbeat logger (wired for future hardware health checks) |

### `PrintBit.Shared`
Cross-cutting types with no dependencies.

- `HardwareSettings` — printer configuration bound from `appsettings.json`
- `IpcSettings` — named pipe configuration for Node error messages

---

## Configuration

`appsettings.json`:

```json
{
  "HardwareSettings": {
    "PrintTimeoutSeconds": 120,
    "PrinterName": "EPSON L5290 Series",
    "PrintQueueDirectory": "C:\\Users\\printbit\\printbit-worker\\queue"
  },
  "IpcSettings": {
    "PipeName": "printbit-node-errors",
    "MaxMessageBytes": 8192
  }
}
```

| Key | Default | Description |
|---|---|---|
| `PrintTimeoutSeconds` | `120` | Print timeout in seconds |
| `PrinterName` | `EPSON L5290 Series` | Windows printer name |
| `PrintQueueDirectory` | `C:\\Users\\printbit\\printbit-worker\\queue` | Directory watched for PDFs |
| `IpcSettings.PipeName` | `printbit-node-errors` | Named pipe for Node error messages |
| `IpcSettings.MaxMessageBytes` | `8192` | Max bytes per error line |

---

## Print Pipeline

`PrintService` dispatches to `SumatraPDF.exe`:

```
SumatraPDF.exe -print-to "<PrinterName>" -print-settings "<copies>" "<filePath>"
```

- Printer: `EPSON L5290 Series`
- Concurrency: serialized via `SemaphoreSlim(1, 1)` — one job at a time
- Timeout: 2 minutes via linked `CancellationTokenSource`
- Exit code `!= 0` → `PrintJobResult { Success = false }`

`SumatraPDF.exe` must be on `PATH` or in the working directory.

---

## Running Locally

```bash
# Development
cd src/PrintBit.HardwareService
dotnet run
```

### Install as Windows Service

```bash
dotnet publish -c Release -o ./publish
sc create PrintBitHardware binPath="C:\path\to\publish\PrintBit.HardwareService.exe"
sc start PrintBitHardware
```

The project references `Microsoft.Extensions.Hosting.WindowsServices` — the host automatically handles Windows SCM lifecycle signals.

---

## Known Gaps / In Progress

| Area | Status |
|---|---|
| `HopperDevice` / `IHopper` | Stub — dispense logic not implemented (not wired in printer-only runtime) |
| `EpsonPrinterDevice` / `IPrinterDevice` | Stub — direct WIA/ESC-P integration not implemented |
| `CoinAcceptorDevice` / `ICoinAcceptor` | Stub — direct Arduino path not implemented (not wired) |
| `HardwareStateMachine` / `PrintJobStateMachine` | Stubs — merged into `TransactionStateMachine` for now (not wired) |
| `TransactionService` | Stub — persistence not wired |
| `NamedPipeServer` / `SocketServer` / `MessageDispatcher` | Stubs — legacy IPC server unused; error pipe uses `ErrorPipeHostedService` |
| Shared DTOs (`TransactionDto`, `HardwareStatusDto`, etc.) | Empty — not yet used |
| `HopperDispenseHandler` / `PrintCompletedHandler` | Stubs — post-print change flow not wired |

---

## Dependencies

| Package | Version | Used In |
|---|---|---|
| `Microsoft.Extensions.Hosting` | 10.0.8 | HardwareService |
| `Microsoft.Extensions.Hosting.WindowsServices` | 10.0.8 | HardwareService |
| `Microsoft.Extensions.Logging` | 10.0.8 | Application, Hardware, Infrastructure |
| `System.IO.Ports` | 10.0.8 | Infrastructure, Hardware |
| `System.Text.Json` | 10.0.8 | HardwareService |
| `Serilog` + `Serilog.Sinks.File` | 4.3.1 / 7.0.0 | HardwareService |

---

## Project Structure

```
src/
├── PrintBit.Application/
│   ├── Events/              # CoinInsertedEvent, StartPrintEvent
│   ├── Handlers/            # CoinInsertedHandler, StartPrintHandler
│   ├── Queues/              # HardwareEventQueue (Channel<Esp32Message>)
│   ├── Services/            # HardwareOrchestrator
│   └── StateMachine/        # TransactionStateMachine
├── PrintBit.Hardware/
│   └── Devices/
│       ├── ESP32/           # Esp32Device, Esp32Message, IEsp32Device
│       ├── CoinAcceptor/    # (stub)
│       ├── Hopper/          # (stub)
│       └── Printer/         # (stub)
├── PrintBit.HardwareService/
│   ├── Services/            # PrintQueueWatcherService, ErrorPipeHostedService
│   └── Program.cs           # DI registration
├── PrintBit.Infrastructure/
│   └── Services/
│       ├── PrintService/    # IPrintService, PrintService (SumatraPDF)
│       ├── SerialService/   # ISerialConnection, SerialConnection (unused)
│       ├── WatchdogService/ # WatchdogService (unused)
│       ├── IPC/             # Node error parsing helpers + legacy stubs
│       └── TransactionService/ # (stub)
└── PrintBit.Shared/
    ├── Configurations/      # HardwareSettings
    ├── Constants/           # (stub)
    ├── Enums/               # TransactionState
    └── Models/              # (stubs: DTOs)
```