using PrintBit.HardwareService.Services;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;
using PrintBit.Infrastructure.Windows.PrinterMonitoring;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<HardwareSettings>(builder.Configuration.GetSection("HardwareSettings"));

builder.Services.Configure<IpcSettings>(builder.Configuration.GetSection("IpcSettings"));

builder.Services.AddHostedService<ErrorPipeHostedService>();

builder.Services.AddHostedService<PrintQueueWatcherService>();

builder.Services.AddHostedService<PrinterMonitorService>();

builder.Services.AddSingleton<IPrintService, PrintService>();

builder.Services.AddSingleton<IPrintRecoveryService, PrintRecoveryService>();

var host = builder.Build();

host.Run();