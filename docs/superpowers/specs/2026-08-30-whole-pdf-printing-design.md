# Whole-PDF Printing Design

## Status

Approved on 2026-08-30.

## Context

PrintBit currently splits each source PDF into one-page PDFs, starts SumatraPDF once for every page and copy, verifies every resulting spooler job, and runs a post-clear hardware guard after every page. This creates repeated Sumatra cold starts and repeated guard delays that make multi-page printing unacceptably slow.

The split files provide deterministic logical page boundaries, but Windows does not provide a physical paper-exit sensor. `Win32_PrintJob.PagesPrinted` is useful progress telemetry but may be zero or lag behind the printer. Page-level files therefore do not guarantee that a sheet physically exited the printer.

## Decision

Print the original PDF without splitting it. Submit one whole-document spooler job per requested copy. Keep copies separate so the worker retains an exact copy boundary while removing per-page process and verification overhead.

The worker will:

- Count the source PDF before dispatch with `PdfPageCounter`.
- Validate and normalize the requested page range without creating derivative PDFs.
- Pass the selected pages, color mode, orientation, and one-copy setting directly to SumatraPDF.
- Retain the global `SemaphoreSlim(1, 1)` print lock.
- Track the maximum observed `Win32_PrintJob.PagesPrinted` value for the active copy.
- Continue monitoring spooler error flags and fatal printer hardware errors, pausing and resuming the same spooler job when the printer recovers.
- Run the configured post-clear guard once per whole-document copy.
- Treat a last observed spooler `TotalPages` lower than the expected selected-page count as `IncompleteOutput` after the job clears.
- Treat a healthy cleared job as complete when `TotalPages` is unknown or matches the expected count, even when `PagesPrinted` lags.
- Emit `PrintSucceeded` only when every requested copy completes; partial or terminal failures emit `PrintFailed` with page/copy details.

## Page-Count Semantics

The event contract distinguishes page-count confidence:

- `confirmed`: the whole-document spooler job cleared and the printer remained healthy through the guard window; the worker records the expected selected-page count for that copy.
- `best_effort`: a failed job exposed a positive `PagesPrinted` value; the worker reports the maximum value observed.
- `unknown`: the failed job exposed no usable page progress.

The receipt/refund consumer must not interpret `best_effort` as a physical paper-exit guarantee. Automatic missing-page reprints are out of scope because stale spooler counts could duplicate output.

## Error and Recovery Behavior

- Paper-out, offline, blocked-queue, and user-intervention status flags enter patience mode and emit `JobPaused`.
- When the same job becomes healthy, it emits `JobResumed` and continues; the worker does not submit a replacement job.
- A patience timeout cancels the matching spooler job and emits `PrintFailed`.
- A truncated cleared job, fatal hardware error, process failure, or verification failure emits `PrintFailed` and preserves the source PDF and sidecar in the failed directory.
- A successful job emits `PrintSucceeded`, after which the queue watcher removes the source PDF and sidecar.

## Alternatives Considered

### One spooler job for all copies

This is faster for multi-copy requests, but drivers may represent copy progress inconsistently. Rejected for the initial rollout because per-copy jobs provide a reliable copy boundary.

### Fixed-size page batches

This narrows uncertainty during partial failures but retains repeated process starts and guard windows. Rejected because whole-document-per-copy printing already preserves the most valuable boundary with substantially less latency.

### Keep one-page PDFs

This retains deterministic logical page completion but causes the unacceptable delay this change addresses.

## Compatibility and Rollback

No package or external service is added. SumatraPDF already supports page ranges, copies, collation, orientation, and color through `-print-settings`. Rollback is a Git revert to the page-splitting implementation; no data migration is required.
