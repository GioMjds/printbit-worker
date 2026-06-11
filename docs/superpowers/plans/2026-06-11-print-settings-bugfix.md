# Print Settings Bugfix & Enhancement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the "only prints page 1" bug by explicitly setting `Nx` copies, and enhance the C# worker to read print settings (color, copies, page range, orientation) from a `.json` sidecar file rather than hardcoding.

**Architecture:** We introduce `PrintJobSettings` to map Node.js settings. `PrintQueueWatcherService` will now trigger off `.json` files, deserialize them, and pass the settings along with the corresponding `.pdf` file to `PrintService`. `PrintService` translates these into the correct SumatraPDF CLI arguments.

**Tech Stack:** C#, .NET 10, System.Text.Json

---

### Task 1: Create PrintJobSettings Model & Update PrintJobRequest

**Files:**
- Create: `src/PrintBit.Infrastructure/Services/PrintService/PrintJobSettings.cs`
- Modify: `src/PrintBit.Infrastructure/Services/PrintService/PrintJobRequest.cs:1-10`

- [ ] **Step 1: Create PrintJobSettings model**

Create `src/PrintBit.Infrastructure/Services/PrintService/PrintJobSettings.cs`:

```csharp
namespace PrintBit.Infrastructure.Services.PrintService;

public class PrintJobSettings
{
    public int Copies { get; set; } = 1;
    public bool Color { get; set; } = false;
    public string? PageRange { get; set; }
    public string? Orientation { get; set; }
}
```

- [ ] **Step 2: Update PrintJobRequest**

Update `src/PrintBit.Infrastructure/Services/PrintService/PrintJobRequest.cs` to use the settings. Replace the entire file with:

```csharp
namespace PrintBit.Infrastructure.Services.PrintService;

public class PrintJobRequest
{
    public string FilePath { get; set; } = string.Empty;
    public string PrinterName { get; set; } = string.Empty;
    public PrintJobSettings Settings { get; set; } = new();
}
```

- [ ] **Step 3: Run existing tests to verify build fails**

Run: `dotnet test tests/PrintBit.Tests/`
Expected: Build failures in `PrintService.cs` and `PrintQueueWatcherService.cs` because `request.Copies` no longer exists.

### Task 2: Update PrintService Command Formatting

**Files:**
- Modify: `src/PrintBit.Infrastructure/Services/PrintService/PrintService.cs:260-281`

- [ ] **Step 1: Update BuildPrintProcess to map SumatraPDF arguments**

In `src/PrintBit.Infrastructure/Services/PrintService/PrintService.cs`, replace the `BuildPrintProcess` method:

```csharp
    internal static Process BuildPrintProcess(
        string sumatraPath,
        PrintJobRequest request)
    {
        var settingsList = new List<string>
        {
            $"{Math.Max(1, request.Settings.Copies)}x",
            request.Settings.Color ? "color" : "monochrome"
        };

        if (!string.IsNullOrWhiteSpace(request.Settings.PageRange))
        {
            settingsList.Add(request.Settings.PageRange);
        }

        if (!string.IsNullOrWhiteSpace(request.Settings.Orientation))
        {
            settingsList.Add(request.Settings.Orientation);
        }

        var printSettingsArg = string.Join(",", settingsList);

        return new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = sumatraPath,
                Arguments =
                    $"-print-to \"{request.PrinterName}\" " +
                    $"-print-settings \"{printSettingsArg}\" " +
                    $"-silent " +
                    $"\"{request.FilePath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };
    }
```

- [ ] **Step 2: Build the project to verify errors are resolved (except queue watcher)**

Run: `dotnet build src/PrintBit.Infrastructure/`
Expected: Build success.

### Task 3: Update Queue Watcher to Trigger on JSON

**Files:**
- Modify: `src/PrintBit.HardwareService/Services/PrintQueueWatcherService.cs:57-166`
- Modify: `src/PrintBit.Infrastructure/PrintBit.Infrastructure.csproj` (add `System.Text.Json` if needed, but it's built-in)

- [ ] **Step 1: Update PrintQueueWatcherService JSON Parsing**

At the top of `src/PrintBit.HardwareService/Services/PrintQueueWatcherService.cs`, add:
```csharp
using System.Text.Json;
```

Inside the `ExecuteAsync` while loop, replace the `Directory.GetFiles(queueDirectory, "*.pdf")` and the processing block with logic to read `.json` files:

```csharp
                var jsonFiles = Directory.GetFiles(queueDirectory, "*.json");

                foreach (var jsonFile in jsonFiles)
                {
                    if (_processingFiles.Contains(jsonFile))
                    {
                        continue;
                    }

                    _processingFiles.Add(jsonFile);

                    try
                    {
                        await Task.Delay(1000, stoppingToken);

                        var pdfFile = Path.ChangeExtension(jsonFile, ".pdf");
                        if (!File.Exists(pdfFile))
                        {
                            _logger.LogWarning("Found JSON sidecar {jsonFile} but missing PDF file. Skipping.", jsonFile);
                            continue;
                        }

                        _logger.LogInformation("Detected print job: {pdfFile} with settings {jsonFile}", pdfFile, jsonFile);

                        var jsonContent = await File.ReadAllTextAsync(jsonFile, stoppingToken);
                        var settings = JsonSerializer.Deserialize<PrintJobSettings>(jsonContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new PrintJobSettings();

                        var fileName = Path.GetFileName(pdfFile);
                        var correlation = TryParseCorrelation(fileName);

                        await _eventPipe.SendAsync(
                            new WorkerPrintEvent
                            {
                                Type = WorkerPrintEventType.PrintStarted,
                                TransactionId = correlation.TransactionId,
                                SpoolerCorrelationKey = correlation.SpoolerCorrelationKey,
                                FileName = fileName,
                                PrinterName = _settings.PrinterName
                            },
                            stoppingToken,
                            connectTimeoutMilliseconds: NodeConnectTimeoutMs);

                        var result = await _printService.PrintAsync(
                            new PrintJobRequest
                            {
                                FilePath = pdfFile,
                                PrinterName = _settings.PrinterName,
                                Settings = settings
                            },
                            stoppingToken);

                        if (!result.Success)
                        {
                            _logger.LogError(
                                "Queue print failed | Stage={stage} | Message={message} | File={file}",
                                result.FailureStage,
                                result.Message,
                                pdfFile);
                        }
                        else
                        {
                            _logger.LogInformation("Queue print succeeded: {file}", pdfFile);
                        }

                        await _eventPipe.SendAsync(
                            new WorkerPrintEvent
                            {
                                Type = result.Success
                                    ? WorkerPrintEventType.PrintSucceeded
                                    : WorkerPrintEventType.PrintFailed,
                                TransactionId = correlation.TransactionId,
                                SpoolerCorrelationKey = correlation.SpoolerCorrelationKey,
                                FileName = fileName,
                                PrinterName = _settings.PrinterName,
                                FailureStage = result.Success ? null : result.FailureStage.ToString(),
                                Message = result.Success ? "Print completed" : result.Message
                            },
                            stoppingToken,
                            connectTimeoutMilliseconds: NodeConnectTimeoutMs);

                        var archivePdfPath = Path.Combine(archiveDirectory, Path.GetFileName(pdfFile));
                        var archiveJsonPath = Path.Combine(archiveDirectory, Path.GetFileName(jsonFile));

                        File.Move(pdfFile, archivePdfPath, true);
                        File.Move(jsonFile, archiveJsonPath, true);

                        _logger.LogInformation("Files archived successfully");
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Queue watcher failed while processing file: {file}", jsonFile);
                    }
                    finally
                    {
                        _processingFiles.Remove(jsonFile);
                    }
                }
```

- [ ] **Step 2: Run tests to verify build and test success**

Run: `dotnet test tests/PrintBit.Tests/`
Expected: PASS. The tests use `StubPrintService` or mock inputs that don't rely heavily on the CLI arguments.

### Task 4: Add Unit Tests for CLI Argument Formatting

**Files:**
- Modify: `src/PrintBit.Infrastructure/PrintBit.Infrastructure.csproj:18`
- Modify: `tests/PrintBit.Tests/PrintServiceTests.cs:5`

- [ ] **Step 1: Add InternalsVisibleTo**

Add this to `src/PrintBit.Infrastructure/PrintBit.Infrastructure.csproj` inside a `<ItemGroup>`:
```xml
  <ItemGroup>
    <InternalsVisibleTo Include="PrintBit.Tests" />
  </ItemGroup>
```

- [ ] **Step 2: Add BuildPrintProcess Tests**

Add to `tests/PrintBit.Tests/PrintServiceTests.cs`:
```csharp
    [Fact]
    public void BuildPrintProcess_FormatsArgumentsCorrectly()
    {
        var request = new PrintJobRequest
        {
            FilePath = @"C:\PrintBit\sample.pdf",
            PrinterName = "MyPrinter",
            Settings = new PrintJobSettings
            {
                Copies = 2,
                Color = true,
                PageRange = "1-3",
                Orientation = "landscape"
            }
        };

        var process = PrintService.BuildPrintProcess("SumatraPDF.exe", request);

        Assert.Contains("-print-settings \"2x,color,1-3,landscape\"", process.StartInfo.Arguments);
    }

    [Fact]
    public void BuildPrintProcess_DefaultsToMonochromeAnd1Copy()
    {
        var request = new PrintJobRequest
        {
            FilePath = @"C:\PrintBit\sample.pdf",
            PrinterName = "MyPrinter",
            Settings = new PrintJobSettings()
        };

        var process = PrintService.BuildPrintProcess("SumatraPDF.exe", request);

        Assert.Contains("-print-settings \"1x,monochrome\"", process.StartInfo.Arguments);
    }
```

- [ ] **Step 3: Run final tests**

Run: `dotnet test tests/PrintBit.Tests/`
Expected: PASS.

- [ ] **Step 4: Commit**

Run:
```bash
git add src/ tests/
git commit -m "feat: parse print settings from json sidecar and format sumatrapdf arguments correctly"
```
