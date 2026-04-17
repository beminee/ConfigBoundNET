# Validation

ConfigBoundNET generates a three-layer validation pipeline that runs entirely at startup (when `ValidateOnStart()` is chained) or on first `IOptions<T>` resolution. Every check is emitted as explicit C# code — no reflection, no `Validator.TryValidateObject`.

## Layer 1: Nullability-based required checks

The generator inspects each property's nullability annotation and emits null/whitespace checks for non-nullable reference types:

| Declaration | Validation |
|---|---|
| `public string Conn { get; init; } = default!;` | `IsNullOrWhiteSpace` check (required) |
| `public string? Conn { get; init; }` | No check (optional) |
| `public required string Conn { get; init; }` | `IsNullOrWhiteSpace` check (C# 11 `required` honored) |
| `public int Timeout { get; init; }` | No check (value types always have a default) |
| `public Uri Endpoint { get; init; } = default!;` | Null check (non-nullable reference type) |

String properties get the stricter `IsNullOrWhiteSpace` check because whitespace-only config values are almost always deployment mistakes. Other reference types get a plain `is null` check.

## Layer 2: DataAnnotations

The generator scans each property for `System.ComponentModel.DataAnnotations` attributes and emits matching validation logic.

### Supported attributes

#### `[Range(min, max)]`

Applies to numeric types (`int`, `long`, `double`, `decimal`, etc.).

```csharp
[Range(1, 65535)]
public int Port { get; init; } = 5432;
```

Generated check:
```csharp
if (options.Port < 1 || options.Port > 65535)
    failures.Add("[Section:Port] must be between 1 and 65535.");
```

Applying `[Range]` to a non-numeric type produces **CB0006** at build time.

#### `[StringLength(max, MinimumLength = min)]`

Applies to `string` properties and collections.

```csharp
[StringLength(200, MinimumLength = 5)]
public string DisplayName { get; init; } = default!;
```

Generated check (null-guarded):
```csharp
if (options.DisplayName is not null && (options.DisplayName.Length < 5 || options.DisplayName.Length > 200))
    failures.Add("[Section:DisplayName] must have length between 5 and 200.");
```

#### `[MinLength(n)]` and `[MaxLength(n)]`

Applies to `string` properties and collections. For collections, uses `.Count` (lists/dictionaries) or `.Length` (arrays).

```csharp
[MinLength(1)]
public List<string> AllowedOrigins { get; init; } = new();
```

Generated check:
```csharp
if (options.AllowedOrigins is not null && options.AllowedOrigins.Count < 1)
    failures.Add("[Section:AllowedOrigins] must have minimum length 1.");
```

Applying length attributes to non-string, non-collection types produces **CB0007**.

#### `[RegularExpression(pattern)]`

Applies to `string` properties. The pattern is validated at **compile time** — an invalid regex produces **CB0008** immediately.

```csharp
[RegularExpression(@"^https?://")]
public string Endpoint { get; init; } = default!;
```

The generator emits a precompiled static `Regex` field on the `Validator` class (compiled once, not per call):

```csharp
private static readonly Regex _cb_Regex_Endpoint =
    new(@"^https?://", RegexOptions.Compiled);
```

And the check:
```csharp
if (options.Endpoint is not null && !_cb_Regex_Endpoint.IsMatch(options.Endpoint))
    failures.Add("[Section:Endpoint] does not match the required pattern.");
```

#### `[Url]`

Validates that a string is an absolute URL via `Uri.TryCreate`.

```csharp
[Url]
public string CallbackUrl { get; init; } = default!;
```

#### `[EmailAddress]`

Validates using a precompiled regex matching the BCL's `EmailAddressAttribute` pattern.

```csharp
[EmailAddress]
public string AdminEmail { get; init; } = default!;
```

#### `[Required]`

Explicitly marks a property as required. If the property is already non-nullable, this is redundant and produces **CB0009** (informational). The code fix provider offers to remove it.

#### `[AllowedValues(...)]` and `[DeniedValues(...)]` (.NET 8+)

```csharp
[AllowedValues("development", "staging", "production")]
public string Environment { get; init; } = default!;
```

Generated check:
```csharp
if (options.Environment is not null && !new[] { "development", "staging", "production" }.Contains(options.Environment))
    failures.Add("[Section:Environment] must be one of: development, staging, production.");
```

### Custom error messages (`ErrorMessage`)

Every supported annotation honors the standard `ErrorMessage` named argument. The string can contain `{0}`, `{1}`, and `{2}` placeholders that are substituted **at generator time** (not runtime), producing zero-allocation string literals.

```csharp
[Range(1, 65535, ErrorMessage = "Port {0} must be between {1} and {2}.")]
public int Port { get; init; } = 5432;
```

Placeholder meanings:
- `{0}` — the `[SectionName:PropertyName]` prefix that identifies the config key
- `{1}` — the first argument (min for `[Range]`, length for `[MinLength]`/`[MaxLength]`/`[StringLength]` min, comma-separated values for `[AllowedValues]`/`[DeniedValues]`)
- `{2}` — the second argument (max for `[Range]`, max length for `[StringLength]`)

A `Port = 0` value with the example above produces:
```
Port [Db:Port] must be between 1 and 65535.
```

When `ErrorMessage` is omitted, ConfigBoundNET falls back to its built-in default format (`"[Section:Property] must be between {min} and {max}."`).

Because substitution happens at build time, the runtime code is a plain string-literal `failures.Add("Port [Db:Port] must be...")` — no `string.Format`, no allocations, no reflection. The trade-off is that placeholders must be compile-time constants (you can't reference other properties or runtime values), which matches how `ValidationAttribute` uses these placeholders anyway.

### Null guards

Every string-targeted annotation check is guarded with `options.X is not null &&` so that null values are caught by the Layer 1 required check, not by an NRE in the annotation check.

## Layer 3: Custom validation hook

Every annotated type gets a `partial void ValidateCustom(List<string> failures)` declaration. Implement it for cross-field rules that no single-property attribute can express:

```csharp
[ConfigSection("Db")]
public partial record DbConfig
{
    public string? ConnString { get; init; }
    public string? ConnStringSecretRef { get; init; }
    public int CommandTimeoutSeconds { get; init; } = 30;
    public string? ReplicaConn { get; init; }

    partial void ValidateCustom(List<string> failures)
    {
        // Exactly one of ConnString or ConnStringSecretRef must be set.
        if (ConnString is null && ConnStringSecretRef is null)
            failures.Add("[Db] Either ConnString or ConnStringSecretRef must be set.");

        if (ConnString is not null && ConnStringSecretRef is not null)
            failures.Add("[Db] Set ConnString or ConnStringSecretRef, not both.");

        // Replica requires a higher timeout for failover.
        if (ReplicaConn is not null && CommandTimeoutSeconds < 5)
            failures.Add($"[Db] When ReplicaConn is set, CommandTimeoutSeconds must be >= 5 (got {CommandTimeoutSeconds}).");
    }
}
```

The hook runs **after** all generated checks (null/whitespace + DataAnnotations + nested validation), so you can safely read properties that have already passed their individual checks.

If you don't implement the method, the C# compiler removes the call site entirely — zero runtime cost.

## Nested validation

When a property's type is another `[ConfigSection]`-annotated record, the parent's validator recursively calls the nested type's validator:

```csharp
[ConfigSection("Database")]
public partial record DatabaseConfig
{
    public string ConnectionString { get; init; } = default!;
    public RetryConfig Retry { get; init; } = default!;
}

[ConfigSection("__inner__")]
public partial record RetryConfig
{
    [Range(1, 20)]
    public int MaxAttempts { get; init; } = 3;
}
```

If `MaxAttempts` is set to `99`, the parent's validation produces:

```
[__inner__:MaxAttempts] must be between 1 and 20.
```

The parent's Validator instantiates `RetryConfig.Validator`, calls `.Validate()`, and merges any failures. This means all three validation layers (nullability, DataAnnotations, custom hooks) on the nested type participate in the parent's validation pass.

## Execution order

1. **Null instance check** — `if (options is null) return Fail(...)`
2. **Required-field checks** — non-nullable reference types
3. **DataAnnotations checks** — `[Range]`, `[RegularExpression]`, etc.
4. **Nested config validation** — recursive calls to inner Validators
5. **Custom hook** — `options.ValidateCustom(failures)`
6. **Return** — `Fail(failures)` or `Success`

All failures accumulate in a single `List<string>` so the user sees every problem at once, not one at a time.

## ValidateOnStart vs lazy validation

```csharp
// Validates at host startup — fails fast, recommended for production:
builder.Services.AddOptions<DbConfig>().ValidateOnStart();

// Validates on first IOptions<T>.Value resolution — default behavior:
builder.Services.AddDbConfig(builder.Configuration);
```

`ValidateOnStart()` is strongly recommended. Without it, misconfigured sections won't fail until something first resolves `IOptions<T>.Value`, which could be minutes or hours into the application's lifetime.
