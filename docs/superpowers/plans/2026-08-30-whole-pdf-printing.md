# Whole-PDF Printing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace per-page PDF splitting and dispatch with one original-PDF spooler job per copy while preserving page progress, hardware-error handling, and receipt/refund terminal data.

**Architecture:** `JobOrchestrator` validates the source document and iterates copies rather than pages. `DocumentPrinter` submits the original PDF with normalized Sumatra settings, monitors one spooler lifecycle, and returns best-known page telemetry so the orchestrator can build progress and terminal events.

**Tech Stack:** .NET 10 Windows Worker Service, xUnit, Moq, SumatraPDF CLI, WMI `Win32_PrintJob`, Windows printer health monitoring.

**Spec:** `docs/superpowers/specs/2026-08-30-whole-pdf-printing-design.md`

## Global Constraints

- Keep the 120-second Sumatra timeout unchanged.
- Keep the global `SemaphoreSlim(1, 1)` serialization lock.
- Submit exactly one original-PDF spooler job per requested copy.
- Do not create per-page PDFs or invoke qpdf page splitting.
- Continue using qpdf only as `PdfPageCounter`'s fallback for compressed PDFs.
- Preserve paper-out/offline pause and resume behavior for the same spooler job.
- Treat WMI page progress as best effort on failures; do not automatically reprint missing pages.
- Update `AGENTS.md` whenever documented architecture, configuration, DI, interfaces, or print behavior changes.

---

### Task 1: Whole-document dispatcher contract and behavior

**Files:**
- Create: `src/PrintBit.Infrastructure/Services/PrintService/IDocumentPrinter.cs`
- Create: `src/PrintBit.Infrastructure/Services/PrintService/DocumentPrinter.cs`
- Modify: `src/PrintBit.Infrastructure/Services/PrintService/PagePrintResult.cs`
- Delete: `src/PrintBit.Infrastructure/Services/PrintService/IPagePrinter.cs`
- Delete: `src/PrintBit.Infrastructure/Services/PrintService/PagePrinter.cs`
- Create: `tests/PrintBit.Tests/DocumentPrinterTests.cs`
- Delete: `tests/PrintBit.Tests/PagePrinterTests.cs`

**Interfaces:**
- Consumes: `IPrinterHealthMonitor`, `HardwareSettings`, and `PrintJobSettings`.
- Produces: `IDocumentPrinter.PrintDocumentAsync` plus `PagePrintResult.PagesPrinted`, `TotalPages`, and `PageCountConfidence`.

- [ ] **Step 1: Write failing tests for Sumatra settings and spooler completion**

Test `DocumentPrinter.BuildPrintProcess` with hand-derived `1x,color,1-3,landscape,collate` arguments and the original PDF path. Test that a cleared job whose `TotalPages` is below the selected-page count returns `IncompleteOutput`, while a healthy cleared job returns the expected page count with `confirmed` confidence.

- [ ] **Step 2: Verify RED**

Run `dotnet test tests/PrintBit.Tests/PrintBit.Tests.csproj --filter FullyQualifiedName~DocumentPrinterTests`; compilation must fail because `DocumentPrinter` does not exist.

- [ ] **Step 3: Implement the dispatcher**

Move the existing print lock, process execution, patience mode, error masks, cancellation, and post-clear guard to `DocumentPrinter`. Use `ProcessStartInfo.ArgumentList` with `1x`, normalized selected pages, color/monochrome, validated orientation, and `collate`. Track maximum WMI page progress and return confidence with every terminal result.

- [ ] **Step 4: Verify GREEN and regressions**

Run `dotnet test tests/PrintBit.Tests/PrintBit.Tests.csproj`; all tests must pass after obsolete page-printer tests are migrated.

- [ ] **Step 5: Commit `feat: dispatch whole PDF copies`**

### Task 2: Copy-level orchestration and terminal events

**Files:**
- Modify: `src/PrintBit.Infrastructure/Services/PrintService/JobOrchestrator.cs`
- Modify: `src/PrintBit.Infrastructure/Services/PrintService/PrintJobResult.cs`
- Modify: `src/PrintBit.Infrastructure/IPC/IWorkerEventPipeClient.cs`
- Modify: `src/PrintBit.Infrastructure/IPC/WorkerEventPipeClient.cs`
- Modify: `src/PrintBit.Infrastructure/IPC/WorkerPrintEvent.cs`
- Modify: `src/PrintBit.HardwareService/Program.cs`
- Modify: `tests/PrintBit.Tests/JobOrchestratorTests.cs`
- Modify: `tests/PrintBit.Tests/WorkerPrintEventTests.cs`

**Interfaces:**
- Consumes: `IDocumentPrinter` and `IWorkerEventPipeClient`.
- Produces: one `PrintStarted`, progress/pause/resume events, and exactly one `PrintSucceeded` or `PrintFailed` terminal event with page/copy results and count confidence.

- [ ] **Step 1: Write failing orchestration tests**

Test a three-page, two-copy request and require two dispatcher calls using the original source PDF and pages `[1, 2, 3]`. Capture the terminal event and require `PrintSucceeded` plus six completed entries. Test a partial second-copy failure and require `PrintFailed`, four of six pages, `best_effort`, and `PrintJobResult.Success == false`.

- [ ] **Step 2: Verify RED**

Run `dotnet test tests/PrintBit.Tests/PrintBit.Tests.csproj --filter "FullyQualifiedName~JobOrchestratorTests|FullyQualifiedName~WorkerPrintEventTests"`; it must fail because the orchestrator still splits and dispatches every page.

- [ ] **Step 3: Implement copy-level orchestration**

Remove work-directory creation, qpdf splitting, and split-file lookup. Count the source PDF, normalize the selected pages, build the manifest, and call `IDocumentPrinter` once per copy. Convert progress into completed manifest entries without exceeding the active copy. Implement the pipe interface and emit `PrintSucceeded` only for full success; all partial or terminal failures emit `PrintFailed` and return a failed `PrintJobResult`.

- [ ] **Step 4: Verify GREEN and regressions**

Run `dotnet test tests/PrintBit.Tests/PrintBit.Tests.csproj`; all tests must pass.

- [ ] **Step 5: Commit `feat: orchestrate whole PDF copies`**

### Task 3: Configuration, documentation, and final verification

**Files:**
- Modify: `src/PrintBit.Shared/Configurations/HardwareSettings.cs`
- Modify: `src/PrintBit.HardwareService/appsettings.json`
- Modify: `tests/PrintBit.Tests/WorkerPrintEventTests.cs`
- Modify: `AGENTS.md`

**Interfaces:**
- Consumes: completed dispatcher and orchestrator behavior.
- Produces: configuration and authoritative architecture documentation matching the implementation.

- [ ] **Step 1: Write and verify the failing configuration expectation**

Replace the split-timeout assertion with `HardwareSettings_HasWholeDocumentConfigFields`, retaining qpdf page-count fallback, patience, and post-clear settings while rejecting a split-only setting. Run the filtered test and confirm failure before changing production configuration.

- [ ] **Step 2: Remove split-only configuration and synchronize docs**

Remove `PdfSplitTimeoutSeconds`; retain `QpdfPath`. Update AGENTS.md Sections 2, 3, 5, 6, and 7 for `DocumentPrinter`, one original-PDF job per copy, best-effort telemetry, one guard per copy, terminal events, and DI.

- [ ] **Step 3: Run final verification**

Run `dotnet test tests/PrintBit.Tests/PrintBit.Tests.csproj`, `dotnet build printbit-worker.slnx`, and `git diff --check`; require zero test/build failures and no whitespace errors.

- [ ] **Step 4: Commit `docs: document whole PDF printing architecture`**
