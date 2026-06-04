# Printer Error Codes & Signals (C# Service Worker)

This file summarizes the **error codes and log signals** emitted by the C# printer-only service so the Node.js app can correlate failures and display helpful messages.

## 1. Print pipeline failure stages (authoritative codes)

`PrintService` returns `PrintJobResult` with a `FailureStage` enum:

| FailureStage | When it happens | Typical log/message |
|---|---|---|
| `Validation` | Input validation failed | `Print file does not exist` / `SumatraPDF executable not found` |
| `ProcessStart` | Sumatra process failed to start | `Failed to start print process: <exception>` |
| `ProcessExit` | Sumatra process exited non-zero | `Print process exited with code <n>: <stderr>` |
| `Timeout` | Print exceeded `PrintTimeoutSeconds` | `Print timeout exceeded` |
| `SpoolerVerification` | Spooler lifecycle check failed | `No spooler job observed...` or `did not clear before timeout` |
| `Unexpected` | Any unhandled exception in print flow | `Unhandled print exception` |

**Node guidance:** treat `FailureStage` as the primary error code for print failures and show `Message` as the user-facing detail.

## 2. Printer monitor signals (WMI)

`PrinterMonitorService` logs Windows spooler status (not converted into explicit error codes yet):

- `Printer status | Offline=<bool> Status=<value> Error=<value>`
- `Printer is OFFLINE`
- `Printer error detected: <DetectedErrorState>`
- `Print job | Name=<name> Document=<doc> Status=<status>`

These logs include WMI fields like `PrinterStatus` and `DetectedErrorState`. If you need specific mappings (e.g., **out of paper**), reference Windows `Win32_Printer` documentation for numeric codes.

## 3. Node -> Service error messages (named pipe)

`ErrorPipeHostedService` listens on the named pipe and logs each line-delimited JSON entry:

```json
{
  "message": "Printer spooler error: access denied",
  "code": "SPOOLER_ACCESS",
  "source": "kiosk-ui",
  "transactionId": "PB-20260525-0001",
  "spoolerCorrelationKey": "79f1c2ae-6a1a-46a8-b43f-0a9c0d1ea1d2",
  "stack": "Error: ...",
  "timestampUtc": "2026-05-22T03:21:58Z"
}
```

The service logs these entries at **Error** level with fields: `Source`, `Code`, `Message`, `TransactionId`, `SpoolerCorrelationKey`, `TimestampUtc`, and `Stack`.

## 4. Service -> Node return pipe (named pipe)

The C# worker sends **one JSON object per line** to the Node return pipe (`IpcSettings.WorkerReturnPipeName`).

```json
{
  "type": "PrintFailed",
  "transactionId": "PB-20260525-0001",
  "spoolerCorrelationKey": "79f1c2ae-6a1a-46a8-b43f-0a9c0d1ea1d2",
  "fileName": "PB-20260525-0001_79f1c2ae_1700000000000.pdf",
  "printerName": "EPSON L5290 Series",
  "failureStage": "SpoolerVerification",
  "message": "No spooler job observed for document '...'",
  "timestampUtc": "2026-05-25T01:45:00Z"
}
```

| Field | Required | Notes |
|---|---|---|
| `type` | Yes | `PrintStarted`, `PrintSucceeded`, or `PrintFailed` |
| `timestampUtc` | Yes | ISO-8601 UTC timestamp |
| `transactionId` | No | Parsed from the queue file name |
| `spoolerCorrelationKey` | No | Parsed from the queue file name |
| `fileName` | No | Queue file name (PDF) |
| `printerName` | No | `HardwareSettings.PrinterName` |
| `failureStage` | No | `PrintFailureStage` string when failed |
| `message` | No | Success summary or failure detail |
