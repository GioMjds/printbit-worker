# Node Print Queue Handoff to C# Worker (Design)

Date: 2026-05-22

## Problem Statement

The Node.js/Express PrintBit app currently owns print dispatch and spooler monitoring. The C# Service Worker has been refocused to be the printer-only spooler bridge with a print queue watcher and error pipe. We need a Node-side integration plan to hand off print jobs to the C# worker and log Node-side errors via the worker’s named pipe, plus a markdown guide describing what to change in the Node repo.

## Goals

- Handoff printing from Node to the C# worker by dropping PDFs into the worker’s queue directory.
- Preserve BullMQ print queue tracking/idempotency while making the dispatch stage fire-and-forget.
- Send Node error messages to the C# worker via named pipe, including job identifiers.
- Produce a Node repo guide at `docs/printbit-worker-integration.md`.

## Non-Goals

- No C# worker behavior changes beyond logging extra identifiers (documented as a follow-up).
- No redesign of the Node pricing, payment, or queue schemas.
- No new UI flows or operator dashboards.

## Architecture Overview

Node keeps the BullMQ print queue and worker for orchestration and status events. The dispatch stage becomes a **handoff** that copies or moves the PDF into the C# worker’s `PrintQueueDirectory`. Once the handoff succeeds, the Node job is considered complete (fire-and-forget). Node retains pre-dispatch printer checks to block obvious offline/out-of-paper conditions. Node error messages are sent to the C# worker over a named pipe and also logged locally.

## Components

1. **Worker handoff service (new)**  
   Writes the PDF into the C# worker’s queue directory using temp + rename for atomicity.

2. **Worker error pipe client (new)**  
   Writes line-delimited JSON to the pipe with `message`, `code`, `source`, `timestampUtc`, `transactionId`, and `spoolerCorrelationKey`.

3. **Print queue orchestration changes**  
   Replace the existing dispatch/spooler-monitor stage with the handoff call; mark job complete immediately.

4. **Print queue worker updates**  
   Emit job completion/failure based on handoff result.

5. **Configuration additions**  
   Env vars for worker queue directory and pipe name; optional toggle for pre-dispatch checks.

6. **Docs**  
   New guide: `docs/printbit-worker-integration.md`.

## Data Flow

1. Payment confirmation enqueues a BullMQ print job as today.
2. Worker pulls job and runs pre-dispatch checks (optional).
3. Handoff service copies/moves the PDF into the C# worker queue directory.
4. Worker emits completion events and marks job complete (no spooler monitor).
5. Any failures emit queue failure events and send JSON error to the C# worker pipe.

## Error Handling

- Pre-dispatch checks continue to use existing `PrinterService` error codes.
- Handoff failures add new codes such as:
  - `WORKER_QUEUE_UNAVAILABLE`
  - `WORKER_HANDOFF_FAILED`
  - `WORKER_PIPE_WRITE_FAILED`
- Error pipe logging is best-effort; if it fails, the job still fails and Node logs locally.

## Configuration

Add to Node environment (names can be finalized during implementation):

- `PRINTBIT_WORKER_QUEUE_DIR` (required)
- `PRINTBIT_WORKER_PIPE_NAME` (default `printbit-node-errors`)
- `PRINTBIT_WORKER_PRECHECKS_ENABLED` (default `true`)

## Guide File Content

`docs/printbit-worker-integration.md` should include:

- Purpose and when to use the C# worker handoff
- Required env vars and defaults
- Where the C# worker expects the queue directory
- Error pipe JSON schema + examples
- Operational notes (fire-and-forget semantics, log locations, common failure codes)

## Testing

- Unit tests for handoff service (temp+rename, missing dir).
- Unit tests for error pipe serialization (includes identifiers).
- No new integration tests unless a local named-pipe harness is added.

## Follow-up: C# Worker Logging

Update the C# worker’s `NodeErrorMessage` schema/logging to include `transactionId` and `spoolerCorrelationKey` so Node identifiers appear in service logs.
