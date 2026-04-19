// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using ConfigBoundNET.WebApi.Config;
using Microsoft.Extensions.Options;
using Serilog;

// ─────────────────────────────────────────────────────────────────────────────
// Web API example — demonstrates ConfigBoundNET end-to-end with:
//   • Multiple [ConfigSection] types (Database, Auth, Email, Cors, RateLimiting).
//   • Nested types, DataAnnotations, custom cross-field validation hooks.
//   • [Sensitive] properties redacted transparently in structured logs.
//   • Serilog as the logging backend, configured from appsettings.json.
//
// Run with:    dotnet run
// Test with:   curl http://localhost:5000/diagnostics
//
// What to look for in the console output:
//   1. At startup, each config section is logged with `{@X}` destructuring.
//      Sensitive values (ConnectionString, JwtSecret, Email:Password) appear
//      as "***" without any Serilog-specific configuration on our side —
//      the generator emits IReadOnlyDictionary<string, object?> on each
//      [Sensitive]-bearing type, and Serilog's default destructurer picks
//      it up as the dictionary contract.
//   2. Any subsequent log call that passes a config object through `{@X}`
//      gets the same redaction automatically.
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

// Serilog bootstrap: read from appsettings' "Serilog" section. Any sink /
// enricher wiring beyond Console can be added here in code or purely in
// config. ReadFrom.Services gives loggers access to DI-registered enrichers.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// ── Register all five config sections. Each call:
//      1. Binds the named section via a reflection-free generated constructor.
//      2. Registers a generated IValidateOptions<T> (null-checks + DataAnnotations).
//      3. Wires IOptionsChangeTokenSource so IOptionsMonitor reacts to reloads.
//
//    ValidateOnStart() is chained so any misconfiguration fails fast at
//    startup rather than on first IOptions<T> resolution.
builder.Services
    .AddDatabaseConfig(builder.Configuration)
    .AddAuthConfig(builder.Configuration)
    .AddEmailConfig(builder.Configuration)
    .AddCorsConfig(builder.Configuration)
    .AddRateLimitingConfig(builder.Configuration);

builder.Services.AddOptions<DatabaseConfig>().ValidateOnStart();
builder.Services.AddOptions<AuthConfig>().ValidateOnStart();
builder.Services.AddOptions<EmailConfig>().ValidateOnStart();
builder.Services.AddOptions<CorsConfig>().ValidateOnStart();
builder.Services.AddOptions<RateLimitingConfig>().ValidateOnStart();

builder.Services.AddControllers();

var app = builder.Build();

app.UseSerilogRequestLogging();
app.MapControllers();

// ── Startup log: dump every bound config via Serilog's {@X} destructurer.
//    This is the load-bearing demo of [Sensitive] transparency. Open the
//    console and you'll see ConnectionString = "***", JwtSecret = "***",
//    and Email.Password = "***" — with zero destructuring-policy code here,
//    zero custom JsonConverter, zero logger-specific attribute.
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    logger.LogInformation("Bound config on startup:");
    logger.LogInformation("  Database     = {@Database}", scope.ServiceProvider.GetRequiredService<IOptions<DatabaseConfig>>().Value);
    logger.LogInformation("  Auth         = {@Auth}",     scope.ServiceProvider.GetRequiredService<IOptions<AuthConfig>>().Value);
    logger.LogInformation("  Email        = {@Email}",    scope.ServiceProvider.GetRequiredService<IOptions<EmailConfig>>().Value);
    logger.LogInformation("  Cors         = {@Cors}",     scope.ServiceProvider.GetRequiredService<IOptions<CorsConfig>>().Value);
    logger.LogInformation("  RateLimiting = {@RateLimiting}", scope.ServiceProvider.GetRequiredService<IOptions<RateLimitingConfig>>().Value);
}

app.Run();

// Program is implicitly a top-level-statements synthetic type; declaring a
// partial here gives WebApplicationFactory-based tests something to anchor on
// if someone adds them later. Not strictly required today, but cheap.
public partial class Program;
