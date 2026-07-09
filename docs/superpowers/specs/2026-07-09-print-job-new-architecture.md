# Print Pipeline Architecture — Page-Level Dispatch with Pause/Resume/Cancel

> **Status:** Design approved
> **Date:** 2026-07-09
> **Scope:** Full rewrite of print pipeline in PrintBit.HardwareService (C# worker)

---

## 1. Overview

Replace the current all-or-nothing print pipeline with a **page-by-page dispatch model** that gives
the system granular control over every page. When a printer error occurs (paper out, jam, door open),
the system **pauses** and waits for a human to physically fix the printer. The kiosk UI is completely
passive during printing — all interaction happens via the Epson's physical buttons:

- **Start/Resume ◆** — printer recovers, system auto-continues
- **Stop/Cancel ✖** — spooler job cancelled, system abandons remaining pages

After completion (full or partial), the kiosk shows a receipt with accurate per-page results.

### Design Principles

- **Page-level granularity** — each page is an independent entity with its own lifecycle
- **Printer-button-driven interaction** — no UI buttons during printing, only passive status display
- **Tiered intervention** — customer fixes simple issues (load paper), attendant handles complex ones
- **Simplified verification** — single-page spooler jobs are trivially verifiable (expected = 1)
- **In-memory state** — no persistence; crash recovery reprints from scratch using the original PDF

---

## 2. System Architecture

### Component Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                    PrintQueueWatcher                        │
│  (watches queue/ dir for JSON sidecars, same as today)      │
└──────────────────────────┬──────────────────────────────────┘
                           │ new job arrives
                           ▼
┌─────────────────────────────────────────────────────────────┐
│                     JobOrchestrator                         │
│  - Splits PDF into single pages via qpdf                    │
│  - Builds ordered print manifest (collated copies)          │
│  - Drives the page-by-page dispatch loop                    │
│  - Owns the pause/resume/cancel state machine               │
│  - Produces the final job result (receipt data)             │
└────────┬────────────────────┬───────────────────────────────┘
         │ dispatch page      │ check health
         ▼                    ▼
┌─────────────────┐  ┌──────────────────────────┐
│  PagePrinter    │  │  PrinterHealthMonitor     │
│  (SumatraPDF    │  │  (WinSpool + WMI polling, │
│   + single-page │  │   Epson popup detection,  │
│   spooler       │  │   error/recovery signals) │
│   verification) │  │                            │
└─────────────────┘  └──────────────────────────┘
         │                    │
         └────────┬───────────┘
                  ▼
┌─────────────────────────────────────────────────────────────┐
│                  WorkerEventPipeClient                       │
│  (sends events to Node.js via named pipe)                   │
└─────────────────────────────────────────────────────────────┘
```

### Component Responsibilities

| Component | Replaces | Responsibility |
|---|---|---|
| `PrintQueueWatcher` | `PrintQueueWatcherService` | File system watcher — mostly unchanged |
| `JobOrchestrator` | **New** | Splits PDF, builds manifest, drives page loop, owns pause/resume/cancel |
| `PagePrinter` | `PrintService` | Prints exactly one page — Sumatra dispatch + simplified spooler verification |
| `PrinterHealthMonitor` | `PrinterMonitorService` + `PrintHealthCoordinator` + `PrintRecoveryService` | Unified health polling — detects errors, recovery, and spooler cancellation signals |
| `WorkerEventPipeClient` | Same | Named pipe to Node.js — extended with new event types |

### What Stays (reused as-is)

- `WinSpoolApi` — P/Invoke layer for printer status
- `PdfPageCounter` — PDF page count parser (no NuGet deps)
- `ErrorPipeHostedService` — Node→Worker error pipe
- Queue file format: `{transactionId}_{spoolerCorrelationKey}_{timestamp}.json/.pdf`

### What Gets Replaced

- `PrintService` → `PagePrinter` + `JobOrchestrator`
- `PrintQueueWatcherService` → slimmed `PrintQueueWatcher`
- `PrinterMonitorService` → merged into `PrinterHealthMonitor`
- `PrintHealthCoordinator` → absorbed into `PrinterHealthMonitor`
- `PrintRecoveryService` → recovery logic moves into `PrinterHealthMonitor`
- `TransactionStateMachine` → replaced by per-page state tracking in `JobOrchestrator`

### DI Registration (new)

```csharp
builder.Services.AddHostedService<PrintQueueWatcher>();
builder.Services.AddHostedService<ErrorPipeHostedService>(); // unchanged

// PrinterHealthMonitor is both a background service (polling loop) and an
// injectable dependency (health queries from JobOrchestrator/PagePrinter).
builder.Services.AddSingleton<PrinterHealthMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PrinterHealthMonitor>());
builder.Services.AddSingleton<IPrinterHealthMonitor>(sp => sp.GetRequiredService<PrinterHealthMonitor>());

builder.Services.AddSingleton<IJobOrchestrator, JobOrchestrator>();
builder.Services.AddSingleton<IPagePrinter, PagePrinter>();
builder.Services.AddSingleton<WorkerEventPipeClient>();
```

All services remain singleton.

---

## 3. Page Lifecycle & State Machine

### Page States

```
Pending ──→ Printing ──→ Completed
                │
                ├──→ Failed       (non-recoverable: Sumatra crash, corrupt PDF)
                │
                └──→ [Job pauses] ──→ Printing  (printer recovered, retry)
                                  └──→ Cancelled (user pressed Stop ✖)

Pending ──→ Cancelled  (remaining pages after Stop or after a Failed page)
```

| State | Meaning |
|---|---|
| `Pending` | Queued in the manifest, not yet dispatched |
| `Printing` | SumatraPDF process active, spooler verification in progress |
| `Completed` | Spooler verified, page physically printed |
| `Failed` | Non-recoverable error — software crash, corrupt PDF, validation failure |
| `Cancelled` | Abandoned by user (Stop button) or by system after a `Failed` page |

### Job States (derived from pages)

| State | Condition |
|---|---|
| `Splitting` | qpdf splitting in progress |
| `Printing` | Actively dispatching pages |
| `Paused` | Hardware error on current page — waiting for human at the printer |
| `Completed` | All pages `Completed` |
| `PartiallyCompleted` | Some pages `Completed`, rest `Cancelled` (user pressed Stop) |
| `Failed` | Non-recoverable error — remaining pages auto-`Cancelled` |

### Page Data Model

```csharp
public enum PagePrintState
{
    Pending,
    Printing,
    Completed,
    Failed,
    Cancelled
}

public class PagePrintEntry
{
    public int PageNumber { get; init; }     // 1-based page in original PDF
    public int CopyNumber { get; init; }     // Which copy (1, 2, 3...)
    public int SequenceIndex { get; init; }  // Position in manifest (0-based)
    public PagePrintState State { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
```

### Print Manifest (collated ordering)

For a 3-page document with 2 copies, the manifest is:

| Sequence | Page | Copy | Description |
|---|---|---|---|
| 0 | 1 | 1 | Copy 1, page 1 |
| 1 | 2 | 1 | Copy 1, page 2 |
| 2 | 3 | 1 | Copy 1, page 3 |
| 3 | 1 | 2 | Copy 2, page 1 |
| 4 | 2 | 2 | Copy 2, page 2 |
| 5 | 3 | 2 | Copy 2, page 3 |

General formula: `copies × pages` entries, ordered as copy 1 all pages, copy 2 all pages, etc.

---

## 4. Print Pipeline Flow

### Complete Lifecycle

```
Queue Watcher detects JSON sidecar
        │
        ▼
  ┌─ Validate ──────────────────────────────────┐
  │  • Companion PDF exists?                     │
  │  • Parse PrintJobSettings (copies, color...) │
  └──────────────────────┬───────────────────────┘
                         │
                         ▼
  ┌─ Split (qpdf) ──────────────────────────────┐
  │  • Create working dir: queue/.work/{jobId}/  │
  │  • qpdf --split-pages input.pdf page.pdf     │
  │  • Produces page-01.pdf, page-02.pdf, ...    │
  │  • Build collated manifest                   │
  └──────────────────────┬───────────────────────┘
                         │
                         ▼
  ┌─ Page Dispatch Loop ────────────────────────┐
  │                                              │
  │  for each entry in manifest:                 │
  │                                              │
  │    ① Pre-flight health check                 │
  │       (WinSpool API + WMI)                   │
  │         ├─ Healthy → continue                │
  │         └─ Unhealthy → PAUSE (see §5)        │
  │                                              │
  │    ② Dispatch SumatraPDF                     │
  │       sumatra -print-to "EPSON L5290"        │
  │         -silent "page-03.pdf"                │
  │                                              │
  │    ③ Verify spooler (simplified)             │
  │       • Expected pages = 1 (always)          │
  │       • Poll WMI for job clear               │
  │       • Post-clear health guard (12s)        │
  │         ├─ Success → mark Completed          │
  │         │   emit PrintProgress → next page   │
  │         ├─ Hardware error → PAUSE (see §5)   │
  │         └─ Software error → mark Failed      │
  │             cancel remaining → job Failed    │
  │                                              │
  └──────────────────────┬───────────────────────┘
                         │
                         ▼
  ┌─ Completion ─────────────────────────────────┐
  │  • Build JobResult from manifest             │
  │  • Emit JobCompleted event to Node.js        │
  │  • Clean up files (see §7)                   │
  └──────────────────────────────────────────────┘
```

### Simplified Spooler Verification

With single-page PDFs, verification becomes trivially reliable:

| Aspect | Current (multi-page) | New (single-page) |
|---|---|---|
| Expected pages | Variable, parsed from PDF | Always `1` |
| Page count stall detection | 10s stall with `PagesPrinted < TotalPages` | Not needed — job either clears or errors |
| WMI lag workaround | Complex "treat as success if healthy" logic | Not needed — single page, clear = done |
| Post-clear hardware guard | 12s health check | **Keep** — Epson popups can still appear delayed |
| Print lock (semaphore) | `SemaphoreSlim(1, 1)` | **Keep** — still one job at a time |

---

## 5. Pause/Resume/Cancel Mechanics

### Core Principle

Don't cancel and re-dispatch on error — **let the spooler handle it**. The verification loop just
needs more patience. When a hardware error occurs, the spooler job stays in the queue, waiting.
The Epson's physical buttons determine the outcome:

```
 SumatraPDF dispatches page 3 → Spooler Job #42 created
                                        │
                              ┌─────────┴──────────┐
                              │                    │
                         Paper runs out       Prints OK
                              │                    │
                     Spooler Job #42          Job clears
                     stuck in error           → Completed ✓
                              │
              ┌───────────────┴───────────────┐
              │                               │
    User loads paper                  User presses Stop ✖
    + presses Start ◆                 on Epson
              │                               │
    Spooler Job #42                   Spooler Job #42
    resumes & completes               cancelled/deleted
              │                               │
         → Completed ✓                  → Cancelled ✗
                                     (remaining pages
                                      also Cancelled)
```

### Extended Verification Loop (Patience Mode)

Today's verification loop has a 45-second timeout and immediately fails on hardware errors.
The new loop replaces this with a patience mode:

| Phase | Duration | Behavior |
|---|---|---|
| Normal verification | First 45s | Same as today — poll spooler job, check health |
| Patience mode | Hardware error → extends up to `PauseTimeoutMinutes` (default 15 min) | Keep polling, don't fail. Emit `JobPaused` event. |
| Safety timeout | After max patience | Auto-cancel remaining pages. Handles "user walked away". |

During patience mode, each 2-second poll checks:

| Signal | Detection Method | Result |
|---|---|---|
| Spooler job completes | Job clears from WMI queue after transitioning through a printing state | Page → `Completed`. Exit patience. Next page. |
| Spooler job cancelled | Job disappears without transitioning through a printing state | Page → `Cancelled`. All remaining → `Cancelled`. |
| Printer recovers, job still queued | WinSpool healthy + job in queue | Keep waiting — spooler should complete momentarily. |
| Safety timeout | `PauseTimeoutMinutes` exceeded | Same as cancel — treat as abandoned. |

**Distinguishing completion from cancellation:** Both events look similar in WMI (the job
disappears). The key signal is whether the spooler job ever transitioned through an active
printing state (e.g., `StatusMask` shows `PRINTING` or `PRINTED`) after the error cleared.
If the verification loop observed the job in an active state before it disappeared, that is
a completion. If the job simply vanishes from the error state, that is a cancellation (the
user pressed Stop ✖).

### Pre-Flight Pause

If the printer is already unhealthy when the next page is due (e.g., paper ran out between pages):

1. Pre-flight health check → unhealthy
2. Emit `JobPaused` event to Node.js
3. Poll every 2s:
   - Printer becomes healthy → dispatch page normally
   - Safety timeout → cancel remaining pages

In the pre-flight case there is no spooler job, so the Stop button has no effect.
The only exits are recovery or timeout.

---

## 6. Event Communication

### Event Types

| Event Type | Replaces | When Emitted |
|---|---|---|
| `PrintStarted` | Same | Job begins (before PDF splitting) |
| `PrintProgress` | Same | Each page completes successfully |
| `JobPaused` | **New** | Hardware error, entering patience mode |
| `JobResumed` | **New** | Printer recovered, resuming dispatch |
| `JobCompleted` | `PrintSucceeded` + `PrintFailed` | Job finished with full manifest |
| `PrinterOffline` | Same | General printer offline (not job-specific) |
| `PrinterOnline` | Same | General printer online |
| `PrinterError` | Same | Non-job-specific hardware error |

### Event Payloads

**PrintProgress:**

```json
{
  "type": "PrintProgress",
  "transactionId": "PB-20260709-0001",
  "spoolerCorrelationKey": "79f1c2ae-...",
  "pageNumber": 3,
  "copyNumber": 1,
  "completedCount": 5,
  "totalCount": 15,
  "timestampUtc": "2026-07-09T01:15:00Z"
}
```

**JobPaused:**

```json
{
  "type": "JobPaused",
  "transactionId": "PB-20260709-0001",
  "spoolerCorrelationKey": "79f1c2ae-...",
  "failedPageNumber": 3,
  "failedCopyNumber": 1,
  "completedCount": 4,
  "totalCount": 15,
  "errorMessage": "Paper out detected",
  "timestampUtc": "2026-07-09T01:15:30Z"
}
```

**JobResumed:**

```json
{
  "type": "JobResumed",
  "transactionId": "PB-20260709-0001",
  "spoolerCorrelationKey": "79f1c2ae-...",
  "resumingPageNumber": 3,
  "resumingCopyNumber": 1,
  "completedCount": 4,
  "totalCount": 15,
  "timestampUtc": "2026-07-09T01:16:00Z"
}
```

**JobCompleted (receipt payload):**

```json
{
  "type": "JobCompleted",
  "transactionId": "PB-20260709-0001",
  "spoolerCorrelationKey": "79f1c2ae-...",
  "printerName": "EPSON L5290 Series",
  "outcome": "partially_completed",
  "totalPages": 5,
  "totalCopies": 3,
  "totalExpected": 15,
  "completedCount": 7,
  "cancelledCount": 8,
  "failedCount": 0,
  "pages": [
    { "page": 1, "copy": 1, "state": "completed" },
    { "page": 2, "copy": 1, "state": "completed" },
    { "page": 3, "copy": 1, "state": "completed" },
    { "page": 1, "copy": 2, "state": "completed" },
    { "page": 2, "copy": 2, "state": "completed" },
    { "page": 3, "copy": 2, "state": "completed" },
    { "page": 1, "copy": 3, "state": "completed" },
    { "page": 2, "copy": 3, "state": "cancelled" },
    { "page": 3, "copy": 3, "state": "cancelled" }
  ],
  "startedAt": "2026-07-09T01:14:00Z",
  "completedAt": "2026-07-09T01:18:30Z",
  "timestampUtc": "2026-07-09T01:18:30Z"
}
```

### Job Outcome Values

| `outcome` | Meaning |
|---|---|
| `completed` | All pages printed successfully |
| `partially_completed` | Some pages printed, rest cancelled by user |
| `failed` | Non-recoverable error (corrupt PDF, Sumatra crash) |

### What the Node.js UI Shows

| UI State | Triggered By | Display |
|---|---|---|
| Printing progress | `PrintProgress` | "Printing page 3 of 15..." with progress bar |
| Paused / error | `JobPaused` | "Printer error: load paper. Press Start ◆ to resume, or Stop ✖ to cancel." |
| Resuming | `JobResumed` | "Resuming..." then back to progress |
| Receipt | `JobCompleted` | Transaction summary with per-page/per-copy breakdown |

---

## 7. qpdf Integration & File Management

### Deployment

Same pattern as SumatraPDF — standalone binary in the kiosk `bin/` folder:

```
C:\Users\printbit\bin\
  ├── SumatraPDF.exe    (existing)
  └── qpdf.exe          (new — ~5MB standalone)
```

### Configuration

New fields in `HardwareSettings`:

```json
{
  "HardwareSettings": {
    "QpdfPath": "C:\\Users\\printbit\\bin\\qpdf.exe",
    "PdfSplitTimeoutSeconds": 30,
    "PauseTimeoutMinutes": 15,
    "PrinterName": "EPSON L5290 Series",
    "PrintQueueDirectory": "C:\\Users\\printbit\\printbit-worker\\queue",
    "FailedDirectory": "C:\\Users\\printbit\\printbit-worker\\failed",
    "SumatraPath": "C:\\Users\\printbit\\bin\\SumatraPDF.exe",
    "PrintTimeoutSeconds": 120
  }
}
```

| Setting | Default | Purpose |
|---|---|---|
| `QpdfPath` | `C:\Users\printbit\bin\qpdf.exe` | Path to qpdf binary |
| `PdfSplitTimeoutSeconds` | `30` | Max time for PDF splitting |
| `PauseTimeoutMinutes` | `15` | Safety timeout for pause state |

### File Structure During a Job

```
queue/
  ├── {tx}_{spool}_{ts}.json          ← original sidecar
  ├── {tx}_{spool}_{ts}.pdf           ← original PDF
  └── .work/
      └── {tx}_{spool}/               ← working directory
          ├── page-01.pdf
          ├── page-02.pdf
          ├── page-03.pdf
          ├── page-04.pdf
          └── page-05.pdf
```

### qpdf Invocation

```bash
qpdf --split-pages input.pdf queue/.work/{jobId}/page.pdf
```

Produces `page-01.pdf`, `page-02.pdf`, ..., `page-NN.pdf` (zero-padded).

### Cleanup Rules

| Job Outcome | Original PDF + JSON | Working Directory |
|---|---|---|
| `Completed` | Delete from `queue/` | Delete entirely |
| `PartiallyCompleted` | Delete from `queue/` | Delete entirely |
| `Failed` | Move to `failed/` | Delete entirely |

Split pages are always temporary artifacts. Only the original PDF is preserved on failure.

### Error Handling

| Error | Behavior |
|---|---|
| qpdf exits non-zero | Job → `Failed`. PDF likely corrupt or encrypted. |
| qpdf hangs (>`PdfSplitTimeoutSeconds`) | Kill process. Job → `Failed`. |
| Page count mismatch | Log warning, continue with actual file count. |
| Stale working directory from prior crash | Delete and recreate. |

---

## 8. Scope Summary

### Files to Create (new)

- `JobOrchestrator` — manifest builder, page loop driver, pause/resume/cancel state machine
- `PagePrinter` — single-page SumatraPDF dispatch + simplified spooler verification
- `PrinterHealthMonitor` — unified health polling with error/recovery/cancellation signals
- `PagePrintEntry` / `PagePrintState` — page data model and enum
- New event models: `JobPaused`, `JobResumed`, `JobCompleted`

### Files to Replace

- `PrintService` → replaced by `PagePrinter` + `JobOrchestrator`
- `PrintQueueWatcherService` → replaced by slimmed `PrintQueueWatcher`
- `PrinterMonitorService` → merged into `PrinterHealthMonitor`
- `PrintHealthCoordinator` → absorbed into `PrinterHealthMonitor`
- `PrintRecoveryService` → recovery logic moves into `PrinterHealthMonitor`

### Files to Keep (unchanged)

- `WinSpoolApi` — P/Invoke layer
- `PdfPageCounter` — PDF page count parser
- `ErrorPipeHostedService` — Node→Worker error pipe
- `WorkerEventPipeClient` — extended with new event types but same transport

### External Dependency

- **qpdf** — standalone binary deployed to `C:\Users\printbit\bin\qpdf.exe`