# Print & Copy Worker Handoff Design

Date: 2026-05-25
Status: Approved (brainstorming)

## Summary
Route both **print (QR upload)** and **copy (scanner glass)** jobs through the C# Service Worker by enqueueing a BullMQ print job, preparing a final print-ready PDF in Node, and handing it off via `WORKER_QUEUE_DIR`. The C# worker prints the PDF and reports status through the worker return pipe so the kiosk UI stays accurate.

## Goals
- Use the C# Service Worker as the single print executor for **print** and **copy** jobs.
- Preserve print options (page range, rotation, duplex, color mode, copies) by pre-processing into a final PDF before handoff.
- Keep existing UI/Socket.IO status updates driven by the worker return pipe.
- Align configuration between Node (`PRINTBIT_WORKER_QUEUE_DIR`) and C# (`HardwareSettings.PrintQueueDirectory`).

## Non-Goals
- Moving scanner control into the C# worker (Node continues to scan).
- Rewriting payment logic or pricing analysis.
- Changing the C# print engine (Sumatra PDF remains in use).

## Architecture
- **Node app**: sessions, uploads, scan preview, pricing, payment confirmation.
- **Print Queue (BullMQ)**: authoritative enqueue path for print/copy jobs.
- **Queue worker**: prepares a final PDF and hands off to C# by copying into `WORKER_QUEUE_DIR`.
- **C# Service Worker**: watches queue dir, prints PDF, emits `PrintStarted/PrintSucceeded/PrintFailed`.
- **WorkerReturnPipe**: Node listens and emits Socket.IO events for UI updates.

## Data Flow
### Print (QR upload)
1. `/print` creates a wireless session; phone uploads files to `uploads/`.
2. On confirm payment, Node builds a print job payload and enqueues it to BullMQ.
3. Worker resolves the uploaded file, generates a **final PDF** (conversion + page range + rotation + duplex), then writes it to `WORKER_QUEUE_DIR`.
4. C# worker prints the file and reports lifecycle events via return pipe; Node relays to UI.

### Copy (scanner glass)
1. `/copy` triggers `POST /api/scan/preview` to create a preview PDF in `uploads/scans/`.
2. `POST /api/copy/jobs` enqueues a print job with the preview path and print options.
3. Worker validates the scan file, prepares a final PDF with options, and hands off to `WORKER_QUEUE_DIR`.
4. Release token is cleared only after successful handoff or terminal failure.

## Print-Ready PDF Preparation
- Introduce a dedicated **preparePrintPdf** step in the queue worker to:
  - Convert Office/image inputs to PDF.
  - Apply rotation and page-range selection.
  - Enforce duplex handling by pre-rendering the final pagination order.
  - Output a final PDF path suitable for C# printing.
- The C# worker prints **PDF only** and uses the copies count; all other options are baked into the PDF.

## Configuration
- **Node env**: `PRINTBIT_WORKER_QUEUE_DIR` points to the shared queue folder.
- **C# config**: `HardwareSettings.PrintQueueDirectory` matches the same folder.
- **Named pipes**: `PRINTBIT_WORKER_PIPE_NAME` and `PRINTBIT_WORKER_RETURN_PIPE_NAME` stay unchanged.

## Error Handling
- Pre-processing or handoff failures are **non-retryable** and surface a user-friendly error.
- Worker handoff errors are logged with `transactionId` and `spoolerCorrelationKey`.
- Idempotency keys remain enforced for confirm-payment and copy-job creation.

## Observability
- Keep admin logs for upload/scan events.
- Add worker-handoff logs for conversion and queue copy steps.
- Worker return pipe events remain the canonical status stream for UI.

## Verification
- Unit tests around the print-preparation pipeline (conversion + rotation + page range).
- Integration tests for confirm-payment and copy-job enqueue paths.
- Manual smoke test: upload -> confirm -> job appears in queue dir -> C# prints -> UI receives success.

