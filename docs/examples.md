# Examples

ConfigBoundNET ships with two example projects that demonstrate progressively more complex usage.

## Example 1: Minimal Generic Host (`examples/ConfigBoundNET.Example`)

A single-file console app that binds one config section and prints the values.

**What it demonstrates:**
- Basic `[ConfigSection("Db")]` usage
- Required string (`Conn`) + optional int with default (`CommandTimeoutSeconds`)
- Nullable property (`ReplicaConn`)
- `ValidateOnStart()` for fail-fast startup
- Custom validation hook: if `ReplicaConn` is set, `CommandTimeoutSeconds` must be at least 5

**Run it:**
```bash
cd examples/ConfigBoundNET.Example
dotnet run
```

**Output:**
```
[Db] Conn                   = Server=localhost;Database=App;Trusted_Connection=True;
[Db] CommandTimeoutSeconds  = 30
[Db] ReplicaConn            = (not set)

Validation passed — all cross-field rules satisfied.
```

**Try breaking it:** Edit `appsettings.json` to remove the `Conn` value — the app fails at startup with a clear error message.

---

## Example 2: Web API (`examples/ConfigBoundNET.WebApi`)

A .NET 10 ASP.NET Core Web API with five configuration sections, a diagnostics controller, and environment-specific overrides.

**What it demonstrates:**

### DatabaseConfig — nested types + custom hook
```csharp
[ConfigSection("Database")]
public partial record DatabaseConfig
{
    public string ConnectionString { get; init; } = default!;
    [Range(1, 300)]
    public int CommandTimeoutSeconds { get; init; } = 30;
    [Range(1, 1000)]
    public int MaxPoolSize { get; init; } = 100;
    public bool EnableRetry { get; init; }
    public RetryConfig Retry { get; init; } = default!;

    partial void ValidateCustom(List<string> failures)
    {
        if (EnableRetry && Retry is null)
            failures.Add($"[{SectionName}] EnableRetry is true but the Retry section is missing.");
    }
}
```

### AuthConfig — URL validation + length constraints
```csharp
[ConfigSection("Auth")]
public partial record AuthConfig
{
    [MinLength(32)]
    public string JwtSecret { get; init; } = default!;
    [Url]
    public string Issuer { get; init; } = default!;
    [Url]
    public string Audience { get; init; } = default!;
    [Range(1, 1440)]
    public int TokenLifetimeMinutes { get; init; } = 60;
}
```

### EmailConfig — email validation + TLS credential hook
```csharp
[ConfigSection("Email")]
public partial record EmailConfig
{
    public string SmtpHost { get; init; } = default!;
    [Range(1, 65535)]
    public int SmtpPort { get; init; } = 587;
    [EmailAddress]
    public string SenderAddress { get; init; } = default!;
    [StringLength(100, MinimumLength = 1)]
    public string SenderDisplayName { get; init; } = default!;
    public bool UseTls { get; init; } = true;
    public string? Username { get; init; }
    public string? Password { get; init; }

    partial void ValidateCustom(List<string> failures)
    {
        if (UseTls && string.IsNullOrWhiteSpace(Username))
            failures.Add($"[{SectionName}] Username is required when UseTls is true.");
        if (Username is not null && Password is null)
            failures.Add($"[{SectionName}] Password is required when Username is set.");
    }
}
```

### CorsConfig — collections (List, array, Dictionary) + cross-field rule
```csharp
[ConfigSection("Cors")]
public partial record CorsConfig
{
    [MinLength(1)]
    public List<string> AllowedOrigins { get; init; } = new();
    public string[] AllowedMethods { get; init; } = ["GET"];
    public string[] AllowedHeaders { get; init; } = ["Content-Type", "Authorization"];
    public Dictionary<string, string> ExposedHeaders { get; init; } = new();
    public bool AllowCredentials { get; init; }
    [Range(0, 86400)]
    public int PreflightMaxAgeSeconds { get; init; } = 600;

    partial void ValidateCustom(List<string> failures)
    {
        if (AllowCredentials && AllowedOrigins.Contains("*"))
            failures.Add($"[{SectionName}] AllowCredentials + wildcard origin is rejected by browsers.");
    }
}
```

### RateLimitingConfig — burst-vs-RPM cross-field validation
```csharp
[ConfigSection("RateLimiting")]
public partial record RateLimitingConfig
{
    [Range(1, 10000)]
    public int RequestsPerMinute { get; init; } = 120;
    [Range(1, 1000)]
    public int BurstSize { get; init; } = 30;
    public string? WhitelistedIps { get; init; }

    partial void ValidateCustom(List<string> failures)
    {
        if (BurstSize > RequestsPerMinute)
            failures.Add($"[{SectionName}] BurstSize ({BurstSize}) cannot exceed RequestsPerMinute ({RequestsPerMinute}).");
    }
}
```

### DiagnosticsController — IOptions vs IOptionsMonitor

The controller injects `IOptions<T>` (singleton snapshot) for configs that don't change at runtime, and `IOptionsMonitor<T>` for configs that should react to live reloads:

```csharp
public DiagnosticsController(
    IOptions<DatabaseConfig> db,          // singleton — read once
    IOptions<AuthConfig> auth,
    IOptionsMonitor<EmailConfig> email,   // live — re-reads on file change
    IOptions<CorsConfig> cors,
    IOptionsMonitor<RateLimitingConfig> rateLimiting)
```

### Environment overrides

The project includes `appsettings.json` (production) and `appsettings.Development.json` (dev). The dev config:
- Points SMTP at `localhost:1025` (Mailpit/MailHog)
- Extends token lifetime to 8 hours
- Relaxes rate limits
- Uses `localhost` CORS origins instead of production domains

**Run it:**
```bash
cd examples/ConfigBoundNET.WebApi
dotnet run
```

**Test it:**
```bash
curl http://localhost:5000/diagnostics | jq
```

The diagnostics endpoint returns all five config sections with sensitive values redacted (`JwtSecret` shows `"dev-****"`, passwords show `"***"`).

---

## Patterns demonstrated across both examples

| Pattern | Example 1 | Example 2 |
|---|---|---|
| Basic scalar binding | `string Conn`, `int Timeout` | All configs |
| Nullable/optional properties | `string? ReplicaConn` | `string? WhitelistedIps`, `string? Username` |
| Nested `[ConfigSection]` types | — | `DatabaseConfig.Retry` |
| `[Range]` on numerics | — | Port, timeout, pool size, RPM |
| `[Url]` validation | — | Auth issuer/audience |
| `[EmailAddress]` validation | — | Email sender |
| `[StringLength]` validation | — | Email display name |
| `[MinLength]` on collections | — | CORS allowed origins |
| `List<T>` binding | — | CORS allowed origins |
| `string[]` binding | — | CORS methods, headers |
| `Dictionary<string, string>` binding | — | CORS exposed headers |
| Custom cross-field hooks | Replica + timeout | Retry + EnableRetry, TLS + creds, burst + RPM, wildcard + creds |
| `IOptions<T>` injection | Direct resolution | Controller constructor |
| `IOptionsMonitor<T>` injection | — | Email + rate limiting |
| `ValidateOnStart()` | Yes | Yes |
| Environment-specific overrides | — | Development JSON |
