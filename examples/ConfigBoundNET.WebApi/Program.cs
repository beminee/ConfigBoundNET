// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using ConfigBoundNET.WebApi.Config;

// ─────────────────────────────────────────────────────────────────────────────
// Web API example — demonstrates ConfigBoundNET with multiple configuration
// sections, nested types, DataAnnotations, and custom validation hooks.
//
// Run with:    dotnet run
// Test with:   curl http://localhost:5000/diagnostics
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

// ── Register all four config sections. Each call:
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

app.MapControllers();

app.Run();
