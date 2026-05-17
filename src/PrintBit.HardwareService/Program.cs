using PrintBit.Application.Handlers;
using PrintBit.Application.Services;
using PrintBit.Application.StateMachine;
using PrintBit.Application.Queues;
using PrintBit.Hardware.Devices.ESP32;
using PrintBit.HardwareService;
using PrintBit.HardwareService.Services;
using PrintBit.Infrastructure.Services.SerialService;
using PrintBit.Infrastructure.Services.WatchdogService;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<HardwareSettings>(builder.Configuration.GetSection("HardwareSettings"));

builder.Services.AddHostedService<Worker>();

builder.Services.AddHostedService<HardwareProcessingService>();

builder.Services.AddSingleton<ISerialConnection, SerialConnection>();

builder.Services.AddSingleton<IEsp32Device, Esp32Device>();

builder.Services.AddSingleton<IPrintService, PrintService>();

builder.Services.AddSingleton<StartPrintHandler>();

builder.Services.AddSingleton<WatchdogService>();

builder.Services.AddSingleton<TransactionStateMachine>();

builder.Services.AddSingleton<CoinInsertedHandler>();

builder.Services.AddSingleton<HardwareOrchestrator>();

builder.Services.AddSingleton<HardwareEventQueue>();

var host = builder.Build();

host.Run();