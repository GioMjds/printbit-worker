# Printer-Only Service Worker + Node Error Pipe (Design)

Date: 2026-05-22

## Problem Statement

Refocus the C# Windows Service Worker to printer-only responsibilities: print queue handling and print spooler lifecycle, while accepting error messages from the Node.js + Express.js app and logging them. ESP32 coin/hopper hardware paths should be removed/disabled.

## Goals

- Printer-only worker: keep print queue watching and SumatraPDF-based print execution.
- Receive Node error messages over a named pipe and log them via existing Serilog/ILogger.
- Remove ESP32/coin/hopper processing from runtime and DI wiring.

## Non-Goals

- No UI work in the service.
- No new HTTP endpoints.
- No changes to print job settings beyond existing fixed configuration.

## Architecture Overview

The Windows Service hosts only printer-related background services and a new named-pipe listener for Node errors. Hardware (ESP32) services are removed. Configuration remains in `HardwareSettings` for printer configuration, and a new `IpcSettings` section defines the pipe name.

## Components

1. **PrintQueueWatcherService (existing)**  
   Watches `PrintQueueDirectory` and triggers print execution for new files using existing behavior.

2. **PrintService (existing)**  
   Runs SumatraPDF and verifies spooler lifecycle; serialized by `SemaphoreSlim(1,1)`. Unchanged.

3. **ErrorPipeHostedService (new)**  
   Background service that opens a `NamedPipeServerStream`, reads line-delimited JSON, deserializes to a `NodeErrorMessage` record, validates size/JSON, and logs with `ILogger`.

4. **IpcSettings (new)**  
   Configuration model with `PipeName` (and optional `MaxMessageBytes`).

5. **Program.cs updates**  
   Remove ESP32-related hosted services and singletons (Worker, HardwareProcessingService, HardwareEventQueue, HardwareOrchestrator, serial/Esp32 services, coin/hopper handlers). Register the new IPC hosted service and settings.

## Data Flow

### Print Jobs

1. Node.js drops a file into `PrintQueueDirectory`.
2. PrintQueueWatcherService detects the file and invokes PrintService per existing behavior.
3. PrintService prints and logs success/failure with stage detail.

### Error Messages

1. Node.js opens the named pipe and writes one JSON object per line.
2. ErrorPipeHostedService reads lines until the client disconnects.
3. Each valid JSON line is logged; malformed or oversized messages are logged as warnings and skipped.

## Error Handling & Logging

- Print failures continue to log `Error` with stage detail and file path.
- Pipe listener handles:
  - `JsonException` → log warning and continue.
  - oversized line → log warning (truncate payload) and continue.
  - `IOException` on disconnect → log info/debug and reopen the pipe.
- Unexpected exceptions bubble to the host (no broad catch).

## Message Schema (Node → Service)

Line-delimited JSON with a minimal, optional structure:

```json
{
  "message": "Printer spooler error: access denied",
  "code": "SPOOLER_ACCESS",
  "source": "kiosk-ui",
  "stack": "Error: ...",
  "timestampUtc": "2026-05-22T03:21:58Z"
}
```

Only `message` is required; other fields are optional and logged if present.

## Configuration

Add to `appsettings.json`:

```json
{
  "IpcSettings": {
    "PipeName": "printbit-node-errors"
  }
}
```

Printer settings remain under `HardwareSettings` (PrinterName, PrintQueueDirectory, PrintTimeoutSeconds).

## Testing

- Unit tests for pipe listener parsing/validation:
  - Valid JSON logs expected fields.
  - Invalid JSON logs warning and continues.
  - Oversize line is rejected with warning.
- Keep existing print tests unchanged; no new integration tests unless existing infra supports it.

## Acceptance Criteria

- Service runs with only printer + pipe listener services; no ESP32/coin/hopper processing at runtime.
- Print queue still processes jobs as before.
- Node error messages are logged from the named pipe without crashing the service.
