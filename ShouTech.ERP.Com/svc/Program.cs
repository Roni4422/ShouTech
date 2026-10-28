// ========================================================================
// FILE: svc/Program.cs
// PROJECT: SHOUTECH ERP V10 — Background Windows Service
// ========================================================================

using ShouTech.Svc;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "ShouTech ERP Sync Service";
});

builder.Services.AddSingleton<ServiceHealth>();
builder.Services.AddHostedService<OutboundMessageWorker>();

var host = builder.Build();
host.Run();
