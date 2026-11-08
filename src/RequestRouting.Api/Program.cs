using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RequestRouting.Api;
using RequestRouting.Application;
using RequestRouting.Infrastructure;
using RequestRouting.Infrastructure.Persistence;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication().AddInfrastructure();

builder.Services.AddOptions<QueueOptions>()
    .BindConfiguration(QueueOptions.SectionName)
    .Validate(o => o.UrgentHeadStart >= TimeSpan.Zero, "Queue:UrgentHeadStart must not be negative.")
    .ValidateOnStart();

builder.Services.AddValidation();

// Development defaults to throwing on bad input, which the exception handler below would report as 500.
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = false);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainRuleExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>(tags: ["ready"]);

var app = builder.Build();

// With several instances, run --migrate-only once as a deployment step, before they start. MigrateOnStartup is
// for a single instance: EF Core's migration lock lives inside the database, so it can't protect creating it.
var migrateOnly = args.Contains("--migrate-only");

if (migrateOnly || app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var db = await app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

if (migrateOnly)
{
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") });

app.MapExpertEndpoints();
app.MapRequestEndpoints();

await app.RunAsync();
