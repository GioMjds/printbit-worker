# Print Settings Enhancement & Bug Fix Design

## Context
Currently, the PrintBit C# worker monitors a queue directory for `.pdf` files. It blindly uses `request.Copies` directly in the SumatraPDF `-print-settings` parameter. Due to SumatraPDF's argument parsing, passing just the number `1` (or `request.Copies`) is interpreted as "Print Page 1" rather than "1 copy." This results in only the first page being printed, ignoring the rest of the document.

Additionally, we need to support richer print settings including Color, Page Range, and Orientation, originating from the Node.js application across different modes (Print, Copy, Scan).

## Architecture

To safely pass settings from Node.js to the C# worker without risking race conditions or string-parsing complexity in filenames:
1. Node.js will write the fully generated `.pdf` file to the queue directory first.
2. Node.js will then write a corresponding `.json` sidecar file (e.g., `Tx123_Corr456.json`) containing the structured settings.
3. The C# worker (`PrintQueueWatcherService`) will watch for `.json` files instead of `.pdf` files. This inherently ensures the `.pdf` is completely written to disk before processing starts.

## Data Models

A new model will be introduced in the C# worker:

```csharp
public class PrintJobSettings
{
    public int Copies { get; set; } = 1;
    public bool Color { get; set; } = false; // defaults to monochrome
    public string? PageRange { get; set; }
    public string? Orientation { get; set; } // "portrait" or "landscape"
}
```

`PrintJobRequest` will be updated to hold this object or these expanded properties.

## Command Formatting (SumatraPDF)

`PrintService.BuildPrintProcess` will be updated to map these settings into SumatraPDF's expected `-print-settings` string format:
- **Copies**: Appended as `"{Copies}x"` (e.g., `"1x"`, `"3x"`). This explicit `x` suffix resolves the "only prints page 1" bug.
- **Color**: Appended as `"color"` if true, otherwise `"monochrome"`.
- **PageRange**: Appended if provided (e.g., `"1,3,5-7"`).
- **Orientation**: Appended if provided.

All elements will be joined by commas.
**Example generated string**: `-print-settings "2x,color,landscape,1,3,5-7"`

## Archival Process
Upon completion (success or failure), `PrintQueueWatcherService` will move both the `.pdf` and the `.json` files to the `archive` directory to keep the queue clean.

## Error Handling
- If the `.json` file is picked up but the corresponding `.pdf` file is missing, the worker will log an error and mark the job as failed or retry.
- If the `.json` file cannot be deserialized, the job will fail with a parsing error logged.
