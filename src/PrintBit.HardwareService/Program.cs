using PrintBit.HardwareService;
using PrintBit.Infrastructure.Services.SerialService;
using PrintBit.Infrastructure.Services.WatchdogService;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHostedService<Worker>();

builder.Services.AddSingleton<ISerialConnection, SerialConnection>();

builder.Services.AddSingleton<WatchdogService>();

var host = builder.Build();

host.Run();