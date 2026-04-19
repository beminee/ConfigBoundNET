// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using ConfigBoundNET.WebApi.Config;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ConfigBoundNET.WebApi.Controllers;

/// <summary>
/// Diagnostic endpoint that exposes the current configuration.
/// Demonstrates injecting <see cref="IOptions{TOptions}"/> and
/// <see cref="IOptionsMonitor{TOptions}"/> into a controller — and the fact
/// that <c>[Sensitive]</c>-marked properties redact automatically through
/// both <see cref="System.Text.Json"/> (the HTTP JSON response) and Serilog
/// <c>{@X}</c> templates (the structured log lines).
/// </summary>
/// <remarks>
/// <para>
/// The entire payload body is built by returning the config records
/// <em>directly</em> — no hand-rolled <c>Redact()</c> helper, no anonymous
/// copy-object. ConfigBoundNET's generated
/// <c>IReadOnlyDictionary&lt;string, object?&gt;</c> implementation on each
/// <c>[Sensitive]</c>-bearing type routes STJ through the redacted dictionary
/// path, so the JSON response already has <c>"***"</c> in place of secrets.
/// </para>
/// <para>
/// The <c>_logger.LogInformation("...{@X}", ...)</c> call then proves the same
/// story for Serilog: the <c>{@X}</c> destructurer sees our interface, walks
/// it, and emits redacted values in the structured log.
/// </para>
/// <para>
/// In a real application this controller would be behind an authorization
/// policy. Here it's open so you can <c>curl http://localhost:5000/diagnostics</c>
/// and watch the redaction work.
/// </para>
/// </remarks>
[ApiController]
[Route("[controller]")]
public sealed class DiagnosticsController : ControllerBase
{
    private readonly IOptions<DatabaseConfig> _db;
    private readonly IOptions<AuthConfig> _auth;
    private readonly IOptionsMonitor<EmailConfig> _email;
    private readonly IOptions<CorsConfig> _cors;
    private readonly IOptionsMonitor<RateLimitingConfig> _rateLimiting;
    private readonly ILogger<DiagnosticsController> _logger;

    public DiagnosticsController(
        IOptions<DatabaseConfig> db,
        IOptions<AuthConfig> auth,
        IOptionsMonitor<EmailConfig> email,
        IOptions<CorsConfig> cors,
        IOptionsMonitor<RateLimitingConfig> rateLimiting,
        ILogger<DiagnosticsController> logger)
    {
        _db = db;
        _auth = auth;
        _email = email;
        _cors = cors;
        _rateLimiting = rateLimiting;
        _logger = logger;
    }

    /// <summary>
    /// Returns every bound configuration section. Sensitive values render as
    /// <c>"***"</c> transparently via the generated
    /// <c>IReadOnlyDictionary&lt;string, object?&gt;</c> implementation.
    /// </summary>
    [HttpGet]
    public IActionResult Get()
    {
        var db = _db.Value;
        var auth = _auth.Value;
        var email = _email.CurrentValue;
        var cors = _cors.Value;
        var rl = _rateLimiting.CurrentValue;

        // Structured log: {@X} triggers Serilog's destructuring, which sees
        // our IReadOnlyDictionary<string, object?> implementation and
        // renders each sensitive property as "***" — no policy, no
        // attribute, no custom formatter. Check the console output.
        _logger.LogInformation(
            "Diagnostics request: {@Database} {@Auth} {@Email}",
            db, auth, email);

        // Return the configs directly. ASP.NET Core's default JSON formatter
        // delegates to STJ; STJ picks the ReadOnlyDictionaryConverter for
        // each [Sensitive]-bearing type; sensitive values emit as "***".
        return Ok(new
        {
            Database = db,
            Auth = auth,
            Email = email,
            Cors = cors,
            RateLimiting = rl,
        });
    }
}
