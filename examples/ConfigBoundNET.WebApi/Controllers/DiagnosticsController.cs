// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using ConfigBoundNET.WebApi.Config;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ConfigBoundNET.WebApi.Controllers;

/// <summary>
/// Diagnostic endpoint that exposes the current (redacted) configuration.
/// Demonstrates injecting <see cref="IOptions{TOptions}"/> and
/// <see cref="IOptionsMonitor{TOptions}"/> into a controller.
/// </summary>
/// <remarks>
/// <para>
/// In a real application this controller would be behind an authorization
/// policy. Here it's open so you can <c>curl http://localhost:5000/diagnostics</c>
/// and see the bound values immediately.
/// </para>
/// <para>
/// <see cref="IOptions{TOptions}"/> is a singleton snapshot — it reads once
/// and caches. <see cref="IOptionsMonitor{TOptions}"/> re-reads on every
/// access if the underlying <c>IConfiguration</c> has changed (e.g.
/// <c>appsettings.json</c> was edited while the app was running).
/// </para>
/// </remarks>
[ApiController]
[Route("[controller]")]
public sealed class DiagnosticsController : ControllerBase
{
    private readonly IOptions<DatabaseConfig> _db;
    private readonly IOptions<AuthConfig> _auth;
    private readonly IOptionsMonitor<EmailConfig> _email;
    private readonly IOptionsMonitor<RateLimitingConfig> _rateLimiting;

    public DiagnosticsController(
        IOptions<DatabaseConfig> db,
        IOptions<AuthConfig> auth,
        IOptionsMonitor<EmailConfig> email,
        IOptionsMonitor<RateLimitingConfig> rateLimiting)
    {
        _db = db;
        _auth = auth;
        _email = email;
        _rateLimiting = rateLimiting;
    }

    /// <summary>
    /// Returns a redacted snapshot of every bound configuration section.
    /// Sensitive values are masked so this endpoint is safe to log.
    /// </summary>
    [HttpGet]
    public IActionResult Get()
    {
        var db = _db.Value;
        var auth = _auth.Value;
        var email = _email.CurrentValue;
        var rl = _rateLimiting.CurrentValue;

        return Ok(new
        {
            Database = new
            {
                ConnectionString = Redact(db.ConnectionString),
                db.CommandTimeoutSeconds,
                db.MaxPoolSize,
                db.EnableRetry,
                Retry = db.Retry is not null
                    ? new { db.Retry.MaxAttempts, db.Retry.BackoffSeconds }
                    : null,
            },
            Auth = new
            {
                JwtSecret = Redact(auth.JwtSecret),
                auth.Issuer,
                auth.Audience,
                auth.TokenLifetimeMinutes,
                auth.RefreshTokenLifetimeDays,
            },
            Email = new
            {
                email.SmtpHost,
                email.SmtpPort,
                email.SenderAddress,
                email.SenderDisplayName,
                email.UseTls,
                Username = email.Username is not null ? "***" : "(not set)",
                Password = email.Password is not null ? "***" : "(not set)",
            },
            RateLimiting = new
            {
                rl.RequestsPerMinute,
                rl.BurstSize,
                WhitelistedIps = rl.WhitelistedIps ?? "(none)",
            },
        });
    }

    /// <summary>Masks everything except the first 4 characters.</summary>
    private static string Redact(string value) =>
        value.Length <= 4 ? "****" : string.Concat(value.AsSpan(0, 4), "****");
}
