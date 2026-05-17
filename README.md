# PrintBit Hardware Service

A .NET 10 Windows Service Worker that bridges the ESP32 hardware layer (coin acceptor, hopper) with the print pipeline for the PrintBit kiosk system.

---

## Architecture

```
PrintBit.HardwareService   ← Worker Service host (entry point)
├── PrintBit.Application   ← State machine, orchestration, event handlers
├── PrintBit.Hardware      ← ESP32 device abstraction, message parsing
├── PrintBit.Infrastructure← Serial comms, print dispatch, watchdog
└── PrintBit.Shared        ← Enums, DTOs, configuration models
```

### Request Flow

```
ESP32 (serial) → SerialConnection.DataReceived
  → Esp32Device.ParseMessage()        # COIN:5, HOPPER:DONE, PING
  → HardwareEventQueue.EnqueueAsync() # Channel<Esp32Message>, capacity 1024
  → HardwareProcessingService         # BackgroundService consumer
  → HardwareOrchestrator
  → CoinInsertedHandler → TransactionStateMachine
  → [if ReadyToPrint] StartPrintHandler → PrintService (SumatraPDF)
```

---

## Projects

### `PrintBit.HardwareService`
Worker Service host. Registers all DI services and runs two `BackgroundService` workers.

| Service | Role |
|---|---|
| `Worker` | Connects ESP32 via serial, subscribes to `MessageReceived`, enqueues messages |
| `HardwareProcessingService` | Drains `HardwareEventQueue`, dispatches to `HardwareOrchestrator` |

### `PrintBit.Application`
Business logic layer. No direct I/O dependencies.

| Class | Role |
|---|---|
| `TransactionStateMachine` | Tracks `TransactionState` (Idle → WaitingForCoins → ReadyToPrint → Printing → Completed) and `CurrentBalance` |
| `HardwareOrchestrator` | Routes `Esp32Message` types to the correct handler |
| `CoinInsertedHandler` | Delegates coin events to `TransactionStateMachine.InsertCoin()` |
| `StartPrintHandler` | Drives state machine through print lifecycle; calls `IPrintService` |
| `HardwareEventQueue` | Bounded `Channel<Esp32Message>` (1024 capacity, single-reader) |

### `PrintBit.Hardware`
Hardware abstraction layer.

| Class | Role |
|---|---|
| `Esp32Device` | Wraps `ISerialConnection`; parses raw serial strings into typed `Esp32Message` |
| `Esp32Message` | Typed message: `Type`, `Value`, `Raw`, `TimestampUtc` |
| `Esp32MessageType` | `CoinInserted`, `HopperCompleted`, `Heartbeat`, `Unknown`, etc. |
| `Esp32Command` | Static command strings sent back to ESP32 (`HOPPER_DISPENSE`, `PONG`, etc.) |

### `PrintBit.Infrastructure`
I/O services (serial port, print process, watchdog).

| Class | Role |
|---|---|
| `SerialConnection` | Wraps `System.IO.Ports.SerialPort`; exposes `DataReceived` event |
| `PrintService` | Spawns `SumatraPDF.exe` process with `-print-to`; uses `SemaphoreSlim(1,1)` to serialize jobs; 2-minute timeout |
| `WatchdogService` | Heartbeat logger (wired for future hardware health checks) |

### `PrintBit.Shared`
Cross-cutting types with no dependencies.

- `TransactionState` enum — `Idle`, `WaitingForCoins`, `ReadyToPrint`, `Printing`, `DispensingChange`, `Completed`, `Error`
- `HardwareSettings` — bound from `appsettings.json` via `IOptions<T>`

---

## Configuration

`appsettings.json`:

```json
{
  "HardwareSettings": {
    "Esp32Port": "COM3",
    "Esp32BaudRate": 115200,
    "WatchdogIntervalSeconds": 5
  }
}
```

| Key | Default | Description |
|---|---|---|
| `Esp32Port` | `COM3` | Serial port the ESP32 is connected to |
| `Esp32BaudRate` | `115200` | Must match ESP32 firmware baud rate |
| `WatchdogIntervalSeconds` | `5` | Heartbeat poll interval (seconds) |

---

## ESP32 Serial Protocol

### Inbound (ESP32 → Service)

| Message | Parsed As | Notes |
|---|---|---|
| `COIN:<int>` | `CoinInserted`, `Value = <int>` | Coin denomination in centavos/units |
| `HOPPER:DONE` | `HopperCompleted` | Change dispense complete |
| `PING` | `Heartbeat` | Keepalive from ESP32 |
| _(anything else)_ | `Unknown` | Logged, not processed |

### Outbound (Service → ESP32)

| Constant | Value | Trigger |
|---|---|---|
| `Esp32Command.Ping` | `PONG` | Heartbeat response |
| `Esp32Command.HopperDispense` | `HOPPER_DISPENSE` | Trigger change dispense |
| `Esp32Command.PrinterStart` | `PRINTER_START` | Notify print started |
| `Esp32Command.PrinterComplete` | `PRINTER_COMPLETE` | Notify print done |

---

## Transaction State Machine

```
Idle
 └─[InsertCoin]──► WaitingForCoins
                      └─[balance >= ₱5]──► ReadyToPrint
                                              └─[StartPrinting]──► Printing
                                                                      └─[Complete]──► Completed
                                                                      └─[Fail/Reset]──► Idle
```

`Reset()` from any state returns to `Idle` with `CurrentBalance = 0`.

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

# Override port for dev (no physical ESP32)
# Edit appsettings.Development.json → HardwareSettings.Esp32Port
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
| `HopperDevice` / `IHopper` | Stub — dispense logic not implemented |
| `EpsonPrinterDevice` / `IPrinterDevice` | Stub — direct WIA/ESC-P integration not implemented |
| `CoinAcceptorDevice` / `ICoinAcceptor` | Stub — direct Arduino path not implemented |
| `HardwareStateMachine` / `PrintJobStateMachine` | Stubs — merged into `TransactionStateMachine` for now |
| `TransactionService` | Stub — persistence not wired |
| `NamedPipeServer` / `SocketServer` / `MessageDispatcher` | Stubs — IPC to Node.js kiosk app not implemented |
| Shared DTOs (`TransactionDto`, `HardwareStatusDto`, etc.) | Empty — not yet used |
| `HopperDispenseHandler` / `PrintCompletedHandler` | Stubs — post-print change flow not wired |
| File path for print job | Hardcoded to `C:\PrintBit\sample.pdf` in `HardwareOrchestrator` |

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
│   ├── Services/            # HardwareProcessingService
│   ├── Worker.cs            # ESP32 serial → queue
│   └── Program.cs           # DI registration
├── PrintBit.Infrastructure/
│   └── Services/
│       ├── PrintService/    # IPrintService, PrintService (SumatraPDF)
│       ├── SerialService/   # ISerialConnection, SerialConnection
│       ├── WatchdogService/ # WatchdogService
│       ├── IPC/             # (stubs: NamedPipe, Socket, Dispatcher)
│       └── TransactionService/ # (stub)
└── PrintBit.Shared/
    ├── Configurations/      # HardwareSettings
    ├── Constants/           # (stub)
    ├── Enums/               # TransactionState
    └── Models/              # (stubs: DTOs)
```