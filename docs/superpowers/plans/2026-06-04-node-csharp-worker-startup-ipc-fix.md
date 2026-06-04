# Node.js C# Worker Startup IPC Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Node.js and the C# Hardware Service reliably communicate printer lifecycle, offline, online, and hardware-error events during startup and runtime.

**Architecture:** Keep the existing two-pipe contract: Node sends application errors to C# through `printbit-node-errors`, and C# sends worker/printer events to Node through `printbit-worker-events`. Fix the Node startup crash first, then harden the C# → Node return pipe with explicit event types, listener error handling, retry semantics, and deployment diagnostics.

**Tech Stack:** Node.js 22, TypeScript, Express, Socket.IO, BullMQ 5, Jest, .NET 10 Worker Service, Windows named pipes, WMI printer monitoring, xUnit.

---

## File Structure

- Modify `C:\Users\printbit\printbit\src\modules\print-queue\queue.config.ts` to make BullMQ queue names colon-free.
- Modify `C:\Users\printbit\printbit\src\modules\print-queue\print-queue.service.ts` to make BullMQ job IDs colon-free.
- Modify `C:\Users\printbit\printbit\src\services\worker-return-pipe.ts` to expose readiness and handle socket/server errors.
- Modify `C:\Users\printbit\printbit\src\server.ts` to await return-pipe readiness and map `PrinterError`.
- Modify `C:\Users\printbit\printbit\tests\services\worker-return-pipe.spec.ts` to cover `PrinterError` and pipe reset handling.
- Create `C:\Users\printbit\printbit\tests\modules\print-queue\queue.config.spec.ts` for queue-name assertions.
- Create `C:\Users\printbit\printbit\tests\modules\print-queue\print-queue.service.spec.ts` for job-ID assertions.
- Modify `C:\Users\printbit\printbit-worker\src\PrintBit.Infrastructure.Windows\PrinterMonitoring\PrinterMonitorService.cs` to emit `PrinterError` and retry undelivered events.
- Modify `C:\Users\printbit\printbit-worker\tests\PrintBit.Tests\WorkerPrintEventTests.cs` or create `PrinterMonitorServiceTests.cs` for event payload/retry behavior if the current test helpers support it.
- Modify `C:\Users\printbit\printbit-worker\AGENTS.md` because the documented worker return-pipe event contract changes.

---

## Task 1: Fix BullMQ Queue Names

**Files:**
- Modify: `C:\Users\printbit\printbit\src\modules\print-queue\queue.config.ts`
- Create: `C:\Users\printbit\printbit\tests\modules\print-queue\queue.config.spec.ts`

- [ ] **Step 1: Write the failing queue-name test**

Create `C:\Users\printbit\printbit\tests\modules\print-queue\queue.config.spec.ts`:

```ts
import { queueNames } from '@/modules/print-queue/queue.config';

describe('print queue config', () => {
  it('uses BullMQ-safe queue names without colon separators', () => {
    expect(queueNames.printJobs).toBe('printbit-print-jobs');
    expect(queueNames.printJobAttempts).toBe('printbit-print-attempts');
    expect(queueNames.deadLetter).toBe('printbit-print-dead-letter');

    for (const queueName of Object.values(queueNames)) {
      expect(queueName).not.toContain(':');
      expect(queueName).toMatch(/^[a-z0-9-]+$/);
    }
  });
});
```

- [ ] **Step 2: Run the failing test**

Run:

```powershell
cd C:\Users\printbit\printbit
pnpm test -- tests/modules/print-queue/queue.config.spec.ts --runInBand
```

Expected: FAIL because queue names are still `print:jobs`, `print:attempts`, and `print:dead-letter`.

- [ ] **Step 3: Rename the queue constants**

In `C:\Users\printbit\printbit\src\modules\print-queue\queue.config.ts`, replace:

```ts
export const queueNames = {
  printJobs: 'print:jobs',
  printJobAttempts: 'print:attempts',
  deadLetter: 'print:dead-letter',
} as const;
```

with:

```ts
export const queueNames = {
  printJobs: 'printbit-print-jobs',
  printJobAttempts: 'printbit-print-attempts',
  deadLetter: 'printbit-print-dead-letter',
} as const;
```

- [ ] **Step 4: Verify the test passes**

Run:

```powershell
cd C:\Users\printbit\printbit
pnpm test -- tests/modules/print-queue/queue.config.spec.ts --runInBand
```

Expected: PASS.

---

## Task 2: Fix BullMQ Job IDs

**Files:**
- Modify: `C:\Users\printbit\printbit\src\modules\print-queue\print-queue.service.ts`
- Create: `C:\Users\printbit\printbit\tests\modules\print-queue\print-queue.service.spec.ts`

- [ ] **Step 1: Write the failing job-ID test**

Create `C:\Users\printbit\printbit\tests\modules\print-queue\print-queue.service.spec.ts`:

```ts
import { buildPrintQueueJobId } from '@/modules/print-queue/print-queue.service';

describe('buildPrintQueueJobId', () => {
  it('builds deterministic BullMQ-safe IDs without colons', () => {
    const correlation = {
      transactionId: 'tx:abc',
      idempotencyKey: 'idem:key',
      spoolerCorrelationKey: 'spool:123',
    };

    const first = buildPrintQueueJobId(correlation);
    const second = buildPrintQueueJobId(correlation);

    expect(first).toBe(second);
    expect(first).toMatch(/^printjob-[a-f0-9]{64}$/);
    expect(first).not.toContain(':');
  });
});
```

- [ ] **Step 2: Run the failing test**

Run:

```powershell
cd C:\Users\printbit\printbit
pnpm test -- tests/modules/print-queue/print-queue.service.spec.ts --runInBand
```

Expected: FAIL because `buildPrintQueueJobId` is not exported yet.

- [ ] **Step 3: Add deterministic safe job-ID builder**

In `C:\Users\printbit\printbit\src\modules\print-queue\print-queue.service.ts`, add this import:

```ts
import { createHash } from 'node:crypto';
```

Add this exported function after `PrintQueueServiceError`:

```ts
export function buildPrintQueueJobId(correlation: PrintJobCorrelation): string {
  const digest = createHash('sha256')
    .update(correlation.transactionId)
    .update('\0')
    .update(correlation.idempotencyKey)
    .digest('hex');

  return `printjob-${digest}`;
}
```

Replace the existing job ID construction:

```ts
const jobId = `${payload.correlation.transactionId}:${payload.correlation.idempotencyKey}`;
```

with:

```ts
const jobId = buildPrintQueueJobId(payload.correlation);
```

- [ ] **Step 4: Verify the job-ID test passes**

Run:

```powershell
cd C:\Users\printbit\printbit
pnpm test -- tests/modules/print-queue/print-queue.service.spec.ts --runInBand
```

Expected: PASS.

---

## Task 3: Harden Node Return Pipe Listener

**Files:**
- Modify: `C:\Users\printbit\printbit\src\services\worker-return-pipe.ts`
- Modify: `C:\Users\printbit\printbit\tests\services\worker-return-pipe.spec.ts`

- [ ] **Step 1: Add pipe listener tests**

Append to `C:\Users\printbit\printbit\tests\services\worker-return-pipe.spec.ts`:

```ts
import net from 'node:net';
import { startWorkerReturnPipeServer } from '@/services/worker-return-pipe';

it('maps printer hardware errors separately from offline events', () => {
  const mapped = mapWorkerEventToSocket({
    type: 'PrinterError',
    printerName: 'EPSON L5290 Series',
    failureStage: 'hardware_error',
    message: 'Printer hardware error detected (code 2).',
    timestampUtc: '2026-06-04T00:00:00Z',
  });

  expect(mapped.event).toBe('workerPrinterError');
  expect(mapped.payload.failureStage).toBe('hardware_error');
});

it('logs socket errors instead of throwing raw ECONNRESET errors', async () => {
  const pipeName = `printbit-test-${process.pid}-${Date.now()}`;
  const logger = {
    log: jest.fn(),
    warn: jest.fn(),
    error: jest.fn(),
  };

  const handle = startWorkerReturnPipeServer({
    pipeName,
    maxBytes: 8_192,
    onEvent: jest.fn(),
    logger,
  });

  await handle.ready;

  const client = net.createConnection(`\\\\.\\pipe\\${pipeName}`);
  await new Promise<void>((resolve) => client.once('connect', resolve));
  client.destroy(new Error('simulated reset'));

  await new Promise((resolve) => setTimeout(resolve, 50));
  await handle.close();

  expect(logger.warn).toHaveBeenCalledWith(
    expect.stringContaining('[WORKER_RETURN_PIPE] Socket error:'),
  );
});
```

- [ ] **Step 2: Run the failing listener tests**

Run:

```powershell
cd C:\Users\printbit\printbit
pnpm test -- tests/services/worker-return-pipe.spec.ts --runInBand
```

Expected: FAIL because `PrinterError`, `handle.ready`, `handle.close`, and socket error handling do not exist.

- [ ] **Step 3: Update worker event types and return type**

In `C:\Users\printbit\printbit\src\services\worker-return-pipe.ts`, replace the event type and mapping return type with:

```ts
export type WorkerPrintEventType =
  | 'PrintStarted'
  | 'PrintSucceeded'
  | 'PrintFailed'
  | 'PrinterOffline'
  | 'PrinterOnline'
  | 'PrinterError';

export interface WorkerReturnPipeServerHandle {
  pipePath: string;
  server: net.Server;
  ready: Promise<void>;
  close: () => Promise<void>;
}
```

Replace the `mapWorkerEventToSocket` return type union with:

```ts
event:
  | 'workerPrintStarted'
  | 'workerPrintSucceeded'
  | 'workerPrintFailed'
  | 'workerPrinterOffline'
  | 'workerPrinterOnline'
  | 'workerPrinterError';
```

Add this switch case:

```ts
case 'PrinterError':
  return { event: 'workerPrinterError', payload: evt };
```

- [ ] **Step 4: Replace `startWorkerReturnPipeServer` implementation**

Replace the function body in `C:\Users\printbit\printbit\src\services\worker-return-pipe.ts` with:

```ts
export function startWorkerReturnPipeServer(input: {
  pipeName: string;
  maxBytes: number;
  onEvent: (evt: WorkerPrintEvent) => void;
  logger?: Pick<Console, 'warn' | 'error' | 'log'>;
}): WorkerReturnPipeServerHandle {
  const logger = input.logger ?? console;
  const pipePath = `\\\\.\\pipe\\${input.pipeName}`;

  const server = net.createServer((socket) => {
    let buffer = '';

    socket.on('data', (chunk) => {
      buffer += chunk.toString();
      let index = buffer.indexOf('\n');
      while (index >= 0) {
        const line = buffer.slice(0, index);
        buffer = buffer.slice(index + 1);
        try {
          const evt = parseWorkerEventLine(line, input.maxBytes);
          input.onEvent(evt);
        } catch (err) {
          logger.warn(
            `[WORKER_RETURN_PIPE] Ignored payload: ${
              err instanceof Error ? err.message : String(err)
            }`,
          );
        }
        index = buffer.indexOf('\n');
      }
    });

    socket.on('error', (err) => {
      logger.warn(
        `[WORKER_RETURN_PIPE] Socket error: ${
          err instanceof Error ? err.message : String(err)
        }`,
      );
    });
  });

  let settled = false;
  const ready = new Promise<void>((resolve, reject) => {
    server.once('listening', () => {
      settled = true;
      logger.log(`[WORKER_RETURN_PIPE] Listening on ${pipePath}`);
      resolve();
    });

    server.once('error', (err) => {
      const message = err instanceof Error ? err.message : String(err);
      logger.error(`[WORKER_RETURN_PIPE] Server error: ${message}`);
      if (!settled) {
        settled = true;
        reject(err);
      }
    });
  });

  server.listen(pipePath);

  return {
    pipePath,
    server,
    ready,
    close: () =>
      new Promise<void>((resolve, reject) => {
        if (!server.listening) {
          resolve();
          return;
        }

        server.close((err) => {
          if (err) {
            reject(err);
            return;
          }

          resolve();
        });
      }),
  };
}
```

- [ ] **Step 5: Verify listener tests pass**

Run:

```powershell
cd C:\Users\printbit\printbit
pnpm test -- tests/services/worker-return-pipe.spec.ts --runInBand
```

Expected: PASS and no raw `Error: read ECONNRESET` output.

---

## Task 4: Await Pipe Readiness and Map PrinterError in Node Startup

**Files:**
- Modify: `C:\Users\printbit\printbit\src\server.ts`

- [ ] **Step 1: Await return-pipe readiness**

In `C:\Users\printbit\printbit\src\server.ts`, replace:

```ts
startWorkerReturnPipeServer({
```

with:

```ts
const workerReturnPipe = startWorkerReturnPipeServer({
```

After the `startWorkerReturnPipeServer({ ... });` call, add:

```ts
await workerReturnPipe.ready;
```

- [ ] **Step 2: Add separate PrinterError socket emission**

In the `onEvent` handler in `C:\Users\printbit\printbit\src\server.ts`, after the existing `PrinterOffline` block, add:

```ts
if (evt.type === 'PrinterError') {
  io.emit('printerMalfunction', {
    printError: {
      code: 'PRINTER_HARDWARE_ERROR',
      severity: 'fatal',
      userMessage: 'The printer reported a hardware error. Please ask staff for help.',
      hint: evt.message ?? null,
      canRetry: false,
      canDismiss: false,
    },
  });
}
```

- [ ] **Step 3: Build Node server**

Run:

```powershell
cd C:\Users\printbit\printbit
pnpm run build
```

Expected: PASS with no TypeScript errors.

---

## Task 5: Emit PrinterError and Retry C# Return-Pipe Events

**Files:**
- Modify: `C:\Users\printbit\printbit-worker\src\PrintBit.Infrastructure.Windows\PrinterMonitoring\PrinterMonitorService.cs`

- [ ] **Step 1: Add private event model**

In `PrinterMonitorService.cs`, inside `PrinterMonitorService`, add:

```csharp
private sealed record PrinterPipeEvent(
    string Type,
    string PrinterName,
    string Message,
    string TimestampUtc,
    string? FailureStage = null);
```

Add this field beside the existing state fields:

```csharp
private PrinterPipeEvent? _pendingEvent;
```

- [ ] **Step 2: Replace anonymous status events**

Replace the `PrinterOffline` anonymous object with:

```csharp
QueuePipeEvent(new PrinterPipeEvent(
    Type: "PrinterOffline",
    PrinterName: _hardwareSettings.PrinterName,
    Message: "Printer is offline or unreachable. Check USB/network connection.",
    TimestampUtc: DateTime.UtcNow.ToString("o")));
```

Replace the `PrinterOnline` anonymous object with:

```csharp
QueuePipeEvent(new PrinterPipeEvent(
    Type: "PrinterOnline",
    PrinterName: _hardwareSettings.PrinterName,
    Message: "Printer is back online.",
    TimestampUtc: DateTime.UtcNow.ToString("o")));
```

Replace the hardware-error anonymous object with:

```csharp
QueuePipeEvent(new PrinterPipeEvent(
    Type: "PrinterError",
    PrinterName: _hardwareSettings.PrinterName,
    Message: $"Printer hardware error detected (code {errorState}). Check paper, ink, or connection.",
    TimestampUtc: DateTime.UtcNow.ToString("o"),
    FailureStage: "hardware_error"));
```

- [ ] **Step 3: Add queue and drain helpers**

Add these methods to `PrinterMonitorService.cs`:

```csharp
private void QueuePipeEvent(PrinterPipeEvent evt)
{
    _pendingEvent = evt;
}

private async Task DrainPendingEventAsync(CancellationToken stoppingToken)
{
    if (_pendingEvent is null)
    {
        return;
    }

    if (await SendToReturnPipeAsync(_pendingEvent, stoppingToken))
    {
        _pendingEvent = null;
    }
}
```

At the end of `MonitorPrinterStatusAsync`, after the `foreach` loop, add:

```csharp
await DrainPendingEventAsync(stoppingToken);
```

- [ ] **Step 4: Make pipe sending return delivery status**

Change:

```csharp
private async Task SendToReturnPipeAsync(
    object payload,
    CancellationToken stoppingToken)
```

to:

```csharp
private async Task<bool> SendToReturnPipeAsync(
    PrinterPipeEvent payload,
    CancellationToken stoppingToken)
```

Replace the success path final log with:

```csharp
_logger.LogInformation(
    "[PIPE -> Node] Sent {type} to {pipe}",
    payload.Type,
    pipeName);

return true;
```

In the `TimeoutException` catch block, add `return false;`.

Add this catch block before the general `Exception` catch:

```csharp
catch (UnauthorizedAccessException ex)
{
    _logger.LogWarning(
        ex,
        "[PIPE -> Node] Access denied connecting to {pipe}; ensure Node.js and the worker run under compatible Windows identities",
        pipeName);
    return false;
}
```

In the general `Exception` catch block, add `return false;`.

- [ ] **Step 5: Build C# service**

Run:

```powershell
cd C:\Users\printbit\printbit-worker
dotnet build
```

Expected: PASS.

---

## Task 6: Add C# Tests Where Supported

**Files:**
- Modify: `C:\Users\printbit\printbit-worker\tests\PrintBit.Tests\WorkerPrintEventTests.cs`
- Or create: `C:\Users\printbit\printbit-worker\tests\PrintBit.Tests\PrinterMonitorServiceTests.cs`

- [ ] **Step 1: Inspect existing test seams**

Run:

```powershell
cd C:\Users\printbit\printbit-worker
Get-Content .\tests\PrintBit.Tests\WorkerPrintEventTests.cs
Get-Content .\tests\PrintBit.Tests\TestDoubles.cs
```

Expected: identify whether `PrinterMonitorService` can be tested without live WMI/named pipes.

- [ ] **Step 2: Add payload model test if direct monitor testing is not practical**

If WMI and named pipe seams are not injectable, add a narrow test to `WorkerPrintEventTests.cs` that verifies the shared return-pipe contract includes `PrinterError`:

```csharp
[Fact]
public void PrinterErrorPayload_UsesExpectedContract()
{
    var payload = new
    {
        type = "PrinterError",
        printerName = "EPSON L5290 Series",
        failureStage = "hardware_error",
        message = "Printer hardware error detected (code 2). Check paper, ink, or connection.",
        timestampUtc = "2026-06-04T00:00:00.0000000Z"
    };

    var json = JsonSerializer.Serialize(payload);

    json.Should().Contain("\"type\":\"PrinterError\"");
    json.Should().Contain("\"failureStage\":\"hardware_error\"");
    json.Should().Contain("\"printerName\":\"EPSON L5290 Series\"");
}
```

- [ ] **Step 3: Run C# tests**

Run:

```powershell
cd C:\Users\printbit\printbit-worker
dotnet test
```

Expected: PASS.

---

## Task 7: Update Documentation Contract

**Files:**
- Modify: `C:\Users\printbit\printbit-worker\AGENTS.md`

- [ ] **Step 1: Update worker return-pipe event type list**

In `AGENTS.md`, change the worker return-pipe `type` field row from:

```markdown
| `type` | Yes | `PrintStarted`, `PrintSucceeded`, or `PrintFailed` |
```

to:

```markdown
| `type` | Yes | `PrintStarted`, `PrintSucceeded`, `PrintFailed`, `PrinterOffline`, `PrinterOnline`, or `PrinterError` |
```

- [ ] **Step 2: Add printer telemetry event note**

In the same return-pipe section, add:

```markdown
Printer monitor events use the same line-delimited JSON pipe. `PrinterError` carries `failureStage = "hardware_error"` and a human-readable `message`.
```

- [ ] **Step 3: Verify documentation is consistent**

Run:

```powershell
cd C:\Users\printbit\printbit-worker
Select-String -Path .\AGENTS.md -Pattern 'PrinterError|WorkerReturnPipeName|printbit-worker-events'
```

Expected: `PrinterError` appears in the return-pipe contract and does not contradict the service orientation.

---

## Task 8: End-to-End Startup Validation

**Files:**
- No source file changes.

- [ ] **Step 1: Build both projects**

Run:

```powershell
cd C:\Users\printbit\printbit
pnpm run build
cd C:\Users\printbit\printbit-worker
dotnet build
```

Expected: both builds pass.

- [ ] **Step 2: Start Node first**

Run in terminal 1:

```powershell
cd C:\Users\printbit\printbit
pnpm run start
```

Expected:

```text
[WORKER_RETURN_PIPE] Listening on \\.\pipe\printbit-worker-events
```

No startup failure with `Queue name cannot contain :`.

- [ ] **Step 3: Start C# worker second**

Run in terminal 2:

```powershell
cd C:\Users\printbit\printbit-worker
dotnet run --project .\src\PrintBit.HardwareService\PrintBit.HardwareService.csproj
```

Expected: no repeated return-pipe timeout after Node is listening.

- [ ] **Step 4: Start C# worker first**

Stop both processes. Start C# first, then Node.

Expected: C# logs timeout or access-denied warnings while Node is unavailable, keeps the latest pending printer event, and sends it after Node starts.

- [ ] **Step 5: Validate printer hardware error delivery**

Trigger a printer condition that produces `DetectedErrorState != 0`, such as paper/ink/connection error on the Epson L5290.

Expected C# log:

```text
Printer error detected: 2
[PIPE -> Node] Sent PrinterError to printbit-worker-events
```

Expected Node/socket behavior:

```text
workerPrinterError
printerMalfunction with code PRINTER_HARDWARE_ERROR
```

- [ ] **Step 6: Validate Windows identity if access denied persists**

Run in the Node terminal:

```powershell
whoami
```

Run in the C# worker terminal:

```powershell
whoami
```

Expected: identities are compatible. If C# runs as a Windows Service and Node runs as an interactive user, configure both to run under the same kiosk account or a service account with access to the Node-created named pipe.

---

## Acceptance Criteria

- Node startup no longer fails with `Queue name cannot contain :`.
- Node logs return-pipe socket resets through `[WORKER_RETURN_PIPE] Socket error:` and does not print raw unhandled `ECONNRESET`.
- C# sends `PrinterError` for WMI hardware error states instead of overloading `PrinterOffline`.
- C# retries the latest undelivered printer event after timeout or access-denied failures.
- Node emits `workerPrinterError` and `printerMalfunction` with `PRINTER_HARDWARE_ERROR`.
- `pnpm test`, `pnpm run build`, `dotnet test`, and `dotnet build` pass.
- `AGENTS.md` documents the updated worker return-pipe event contract.

