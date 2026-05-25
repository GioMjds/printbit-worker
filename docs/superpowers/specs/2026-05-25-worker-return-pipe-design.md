# C# Worker -> Node Return Pipe (Design)

Date: 2026-05-25

## Problem Statement

The Node app hands off print jobs to the C# worker and currently has no return channel for print outcomes. We need a named-pipe return channel so the C# worker can notify Node of `PrintStarted`, `PrintSucceeded`, and `PrintFailed` events before UI wiring.

## Goals

- Add a **return named pipe** from C# worker to Node.
- Emit `workerPrintStarted`, `workerPrintSucceeded`, `workerPrintFailed` Socket.IO events in Node.
- Include correlation fields (`transactionId`, `spoolerCorrelationKey`) and failure details (`failureStage`, `message`).
- Keep the channel best-effort and non-blocking.

## Non-Goals

- No DB persistence or receipt updates in Node for these events.
- No UI changes in this phase.

## Architecture Overview

Node hosts the named-pipe **server** and parses line-delimited JSON messages from the C# worker. The C# worker acts as a **client**, connecting per event to write a single JSON line and close. Node maps event types to Socket.IO emissions only.

## Components

1. **Node return pipe server (new)**
   - Listens on `PRINTBIT_WORKER_RETURN_PIPE_NAME` (default e.g. `printbit-worker-events`).
   - Validates payload size and JSON parse, then emits Socket.IO events.

2. **Node startup wiring**
   - Start the pipe server in `server.ts` with access to `io`.

3. **C# worker pipe client (new)**
   - Connects to the named pipe and writes one JSON line per event.
   - Logs warnings on connection failure; does not crash the worker.

4. **C# event emitters**
   - `PrintQueueWatcherService` sends:
     - `PrintStarted` when a file is detected and print begins.
     - `PrintSucceeded` on successful `PrintService` result.
     - `PrintFailed` on failed result or exception (includes `FailureStage` + `Message`).

5. **Docs**
   - Update `docs/printbit-worker-integration.md` and `printer-error-codes.md` with return pipe schema.

## Data Flow

1. C# detects a PDF and emits `PrintStarted`.
2. C# executes print and emits `PrintSucceeded` or `PrintFailed`.
3. Node return pipe server parses the line and emits Socket.IO events.

## Payload Schema (C# -> Node)

Line-delimited JSON:

```json
{
  "type": "PrintFailed",
  "transactionId": "PB-20260525-0001",
  "spoolerCorrelationKey": "79f1c2ae-6a1a-46a8-b43f-0a9c0d1ea1d2",
  "fileName": "PB-20260525-0001_79f1c2ae.pdf",
  "printerName": "EPSON L5290 Series",
  "failureStage": "SpoolerVerification",
  "message": "No spooler job observed for document '...'",
  "timestampUtc": "2026-05-25T01:45:00Z"
}
```

## Error Handling

- Node ignores invalid/oversize payloads with warning logs.
- C# pipe client failures are logged and do not crash the worker.
- No retries beyond next event.

## Testing

- Node unit tests for parsing and event mapping.
- C# unit tests only if existing test infra for worker services; otherwise manual pipe test.
