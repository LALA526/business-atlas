using Atlas.Worker;
using PlatformOps;
using PlatformOps.Jobs;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPlatformOps(builder.Configuration.GetConnectionString("Atlas"));
builder.Services.Configure<JobWorkerOptions>(builder.Configuration.GetSection("Jobs"));
builder.Services.AddHostedService<PostgresJobWorker>();
builder.Services.AddHostedService<PostgresEventDeliveryWorker>();

var host = builder.Build();
host.Run();
