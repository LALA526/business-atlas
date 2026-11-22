using Atlas.Worker;
using PlatformOps;
using PlatformOps.Jobs;
using AccessApplicability.Infrastructure;
using AccessApplicability.Application;
using KnowledgeCore.Infrastructure;
using ReadSearchExplain.Export;
using ReadSearchExplain.Search;
using Assistant;
using Assistant.Infrastructure;
using Assistant.Reasoning;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPlatformOps(builder.Configuration.GetConnectionString("Atlas"));
builder.Services.AddSingleton(new PostgresAccessApplicabilityStore(builder.Configuration.GetConnectionString("Atlas") ?? string.Empty));
builder.Services.AddSingleton(services => new PostgresKnowledgeStore(builder.Configuration.GetConnectionString("Atlas") ?? string.Empty, services.GetRequiredService<PostgresAccessApplicabilityStore>()));
builder.Services.AddSingleton(services => new PostgresReadSearchService(builder.Configuration.GetConnectionString("Atlas") ?? string.Empty, services.GetRequiredService<PostgresKnowledgeStore>(), services.GetRequiredService<PostgresAccessApplicabilityStore>(), builder.Configuration["ReadSearchExplain:CursorKey"] ?? throw new InvalidOperationException("ReadSearchExplain:CursorKey is required.")));
builder.Services.AddSingleton<PolicyService>();
builder.Services.AddAssistant(builder.Configuration);
builder.Services.AddScoped<PostgresExportService>();
builder.Services.AddScoped<IJobHandler, ExportJobHandler>();
builder.Services.AddScoped<IJobHandler, ContentReviewJobHandler>();
builder.Services.AddScoped<IJobHandler, ConversationJobHandler>();
builder.Services.Configure<JobWorkerOptions>(builder.Configuration.GetSection("Jobs"));
builder.Services.AddHostedService<PostgresJobWorker>();
builder.Services.AddHostedService<PostgresEventDeliveryWorker>();

var host = builder.Build();
host.Run();
