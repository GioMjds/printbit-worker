using PrintBit.HardwareService.Services;
using PrintBit.Infrastructure.IPC;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;
using PrintBit.Infrastructure.Windows.PrinterMonitoring;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<HardwareSettings>(builder.Configuration.GetSection("HardwareSettings"));

builder.Services.Configure<IpcSettings>(builder.Configuration.GetSection("IpcSettings"));

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "PrintBitHardware";
});

builder.Services.AddHostedService<ErrorPipeHostedService>();

// Printer monitoring and whole-document spooler dispatch
builder.Services.AddSingleton<PrinterHealthMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PrinterHealthMonitor>());
builder.Services.AddSingleton<IPrinterHealthMonitor>(sp => sp.GetRequiredService<PrinterHealthMonitor>());

builder.Services.AddSingleton<IDocumentPrinter, DocumentPrinter>();
builder.Services.AddSingleton<IJobOrchestrator, JobOrchestrator>();
builder.Services.AddHostedService<PrintQueueWatcher>();

builder.Services.AddSingleton<WorkerEventPipeClient>();
builder.Services.AddSingleton<IWorkerEventPipeClient>(
    sp => sp.GetRequiredService<WorkerEventPipeClient>());

var host = builder.Build();

host.Run();
