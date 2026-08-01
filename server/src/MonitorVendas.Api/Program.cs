using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Common;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Ai;
using MonitorVendas.Api.Features.Ai.Analysis;
using MonitorVendas.Api.Features.Contacts;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Outcomes;
using MonitorVendas.Api.Features.Reconciliation;
using MonitorVendas.Api.Features.ReportExport;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Api.Features.Webhooks;
using MonitorVendas.Api.Integrations.Ai;
using MonitorVendas.Api.Integrations.Evolution;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

builder.Services.AddApiVersioningSetup();
builder.Services.AddProblemDetails();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    if (origins.Length > 0)
        policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
    else if (builder.Environment.IsDevelopment())
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
}));
builder.Services.AddEvolutionApi(builder.Configuration);
builder.Services.AddAiProvider(builder.Configuration);
builder.Services.Configure<AiBudgetOptions>(builder.Configuration.GetSection(AiBudgetOptions.Section));
builder.Services.AddScoped<AiBudget>();
builder.Services.AddScoped<ConversationAiWorkset>();
builder.Services.AddScoped<ConversationAnalyzer>();
builder.Services.AddScoped<SellerSynthesizer>();
builder.Services.AddScoped<AiAnalysisQueries>();
builder.Services.AddScoped<AiJobEstimator>();
builder.Services.AddSingleton<IAiJobRunner, AiJobRunner>();
builder.Services.Configure<AiJobOptions>(builder.Configuration.GetSection(AiJobOptions.Section));
if (builder.Configuration.GetValue("AiJob:Enabled", true))
    builder.Services.AddHostedService<AiJobBackgroundService>();
builder.Services.AddScoped<ReportExportBuilder>();
builder.Services.Configure<PairingOptions>(builder.Configuration.GetSection(PairingOptions.Section));
builder.Services.AddScoped<PairingService>();
if (builder.Configuration.GetValue("Pairing:CleanupEnabled", true))
    builder.Services.AddHostedService<PairingCleanupService>();
builder.Services.Configure<WebhookOptions>(builder.Configuration.GetSection(WebhookOptions.Section));
builder.Services.Configure<MetricsOptions>(builder.Configuration.GetSection(MetricsOptions.Section));
builder.Services.AddSingleton<IWebhookProcessor, WebhookProcessor>();
builder.Services.AddScoped<IWebhookEventHandler, MessageUpsertHandler>();
builder.Services.AddScoped<IWebhookEventHandler, MessageUpdateHandler>();
builder.Services.AddScoped<IWebhookEventHandler, ConnectionUpdateHandler>();
builder.Services.AddScoped<IWebhookEventHandler, QrCodeUpdatedHandler>();
builder.Services.AddScoped<IWebhookEventHandler, LabelsEditHandler>();
builder.Services.AddScoped<IWebhookEventHandler, LabelsAssociationHandler>();
if (builder.Configuration.GetValue("Webhook:ProcessorEnabled", true))
    builder.Services.AddHostedService<WebhookProcessorBackgroundService>();

builder.Services.AddScoped<ReportQueries>();
builder.Services.AddScoped<ContactQueries>();
builder.Services.Configure<ContactShareOptions>(builder.Configuration.GetSection(ContactShareOptions.Section));
builder.Services.AddSingleton<IContactShareSender, ContactShareSender>();
if (builder.Configuration.GetValue("ContactShare:Enabled", true))
    builder.Services.AddHostedService<ContactShareBackgroundService>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ReportCacheVersion>();
builder.Services.AddScoped<ReportCache>();
builder.Services.AddSingleton<IDirtyDayTracker, DirtyDayTracker>();
builder.Services.AddSingleton<OutcomeCatalogVersion>();
builder.Services.AddSingleton<OutcomeLabelMatcher>();
builder.Services.AddScoped<OutcomeResolver>();
builder.Services.AddScoped<OutcomeReconciler>();
builder.Services.AddScoped<DailyMetricsBuilder>();
if (builder.Configuration.GetValue("Metrics:AggregationEnabled", true))
    builder.Services.AddHostedService<MetricsAggregationBackgroundService>();

builder.Services.Configure<ReconciliationOptions>(builder.Configuration.GetSection(ReconciliationOptions.Section));
builder.Services.AddSingleton<IReconciliationService, ReconciliationService>();
if (builder.Configuration.GetValue("Reconciliation:Enabled", true))
    builder.Services.AddHostedService<ReconciliationBackgroundService>();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();

app.MapHealthChecks("/health");

var v1 = app.MapVersionedGroup();
v1.MapGet("/ping", () => Results.Ok(new { status = "ok" }));
v1.MapSellersEndpoints();
v1.MapNumbersEndpoints();
v1.MapPairingEndpoints();
v1.MapWebhookEndpoints();
v1.MapReportsEndpoints();
v1.MapHolidaysEndpoints();
v1.MapOutcomeTypesEndpoints();
v1.MapContactsEndpoints();
v1.MapContactShareEndpoints();
v1.MapAiBudgetEndpoints();
v1.MapReportExportEndpoints();
v1.MapAiAnalysisEndpoints();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

await app.RunAsync();

// Exposto para a WebApplicationFactory dos testes de integração.
public partial class Program;
