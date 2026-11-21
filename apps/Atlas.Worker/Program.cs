using Atlas.Worker;
using PlatformOps;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPlatformOps(builder.Configuration.GetConnectionString("Atlas"));
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
