using PrintBit.HardwareService.Services;
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.DocumentConversion;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;
using PrintBit.Infrastructure.Windows.PowerMonitoring;
using PrintBit.Infrastructure.Windows.PrinterMonitoring;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<HardwareSettings>(builder.Configuration.GetSection("HardwareSettings"));

builder.Services.Configure<IpcSettings>(builder.Configuration.GetSection("IpcSettings"));

builder.Services.Configure<PowerSettings>(builder.Configuration.GetSection("PowerSettings"));

builder.Services.Configure<PrinterRecoverySettings>(builder.Configuration.GetSection("PrinterRecoverySettings"));

builder.Services.Configure<DocumentConversionSettings>(builder.Configuration.GetSection("DocumentConversionSettings"));

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "PrintBitHardware";
});

builder.Services.AddHostedService<ErrorPipeHostedService>();

// Document conversion offline service and IPC pipe
builder.Services.AddSingleton<IDocumentConversionService, LibreOfficeDocumentConversionService>();
builder.Services.AddHostedService<DocumentConversionPipeHostedService>();

// Printer monitoring and whole-document spooler dispatch
builder.Services.AddSingleton<PrinterHealthMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PrinterHealthMonitor>());
builder.Services.AddSingleton<IPrinterHealthMonitor>(sp => sp.GetRequiredService<PrinterHealthMonitor>());

// Printer recovery control plane
builder.Services.AddSingleton<IPrinterOperationCoordinator, PrintOperationCoordinator>();
builder.Services.AddSingleton<IPrintSpoolerController, ServiceControllerSpoolerController>();
builder.Services.AddSingleton<IPrinterRecoveryService, PrinterRecoveryService>();
builder.Services.AddHostedService<WorkerCommandPipeHostedService>();

// Power monitoring and dispatch safety gate
builder.Services.AddSingleton<IPowerStatusProvider, NativePowerStatusProvider>();
builder.Services.AddSingleton<IPowerSafetyGate, PowerSafetyGate>();
builder.Services.AddSingleton<PowerMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PowerMonitorService>());

builder.Services.AddSingleton<IDocumentPrinter, DocumentPrinter>();
builder.Services.AddSingleton<IJobOrchestrator, JobOrchestrator>();
builder.Services.AddHostedService<PrintQueueWatcher>();

builder.Services.AddSingleton<WorkerEventPipeClient>();
builder.Services.AddSingleton<IWorkerEventPipeClient>(
    sp => sp.GetRequiredService<WorkerEventPipeClient>());

var host = builder.Build();

host.Run();
