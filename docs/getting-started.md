# Getting Started

## Installation

ConfigBoundNET ships as a single NuGet analyzer package. There is no separate runtime library — the `[ConfigSection]` attribute and a small `OptionsFactory` helper are emitted directly into your assembly at build time.

```bash
dotnet add package ConfigBoundNET
```

You will also need the standard `IOptions<T>` packages, which are already transitive in most ASP.NET Core and Generic Host applications:

```bash
dotnet add package Microsoft.Extensions.Options.ConfigurationExtensions
```

## Your first config type

Create a partial record annotated with `[ConfigSection]`:

```csharp
using ConfigBoundNET;

namespace MyApp;

[ConfigSection("Database")]
public partial record DatabaseConfig
{
    public string ConnectionString { get; init; } = default!;
    public int CommandTimeoutSeconds { get; init; } = 30;
    public string? ReplicaConnectionString { get; init; }
}
```

That's it. The generator takes over at build time and produces:

1. **`DatabaseConfig.SectionName`** — a `const string` equal to `"Database"`.
2. **`DatabaseConfig(IConfigurationSection)`** — a reflection-free constructor that reads each property from the configuration section.
3. **`DatabaseConfig.Validator`** — an `IValidateOptions<DatabaseConfig>` that null/whitespace-checks `ConnectionString` (non-nullable) and skips `ReplicaConnectionString` (nullable).
4. **`DatabaseConfigServiceCollectionExtensions.AddDatabaseConfig()`** — a DI extension that wires up binding, validation, and reload-on-change.
5. **`partial void ValidateCustom(List<string> failures)`** — a hook you can implement for cross-field rules.

## Registering in Program.cs

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddDatabaseConfig(builder.Configuration)
    .AddOptions<DatabaseConfig>()
    .ValidateOnStart();  // fail fast at startup, not on first resolution

using var app = builder.Build();

var db = app.Services.GetRequiredService<IOptions<DatabaseConfig>>().Value;
Console.WriteLine(db.ConnectionString);
```

## Section name inference

You can omit the section name and let the generator infer it from the type name:

```csharp
[ConfigSection]  // infers "Database" from "DatabaseConfig"
public partial record DatabaseConfig { ... }
```

The generator strips these suffixes (in order): `Configuration`, `Settings`, `Options`, `Config`. So `PaymentsOptions` becomes `"Payments"`, `AppSettings` becomes `"App"`, etc.

If you explicitly pass an empty string (`[ConfigSection("")]`), it's an error (CB0002). Use the parameterless form for inference or supply an explicit name.

## appsettings.json

```json
{
  "Database": {
    "ConnectionString": "Server=localhost;Database=MyApp;",
    "CommandTimeoutSeconds": 60
  }
}
```

Properties absent from the config retain their declared C# defaults. In this example, `ReplicaConnectionString` stays `null` and `CommandTimeoutSeconds` stays `30` if not overridden.

## What happens when validation fails

If `ConnectionString` is missing or empty:

```
Unhandled exception. Microsoft.Extensions.Options.OptionsValidationException:
  [Database:ConnectionString] is required but was null, empty, or whitespace.
```

The error message tells you exactly which section and property failed, so you don't have to trace through stack frames to find the misconfiguration.

## Next steps

- [Validation](validation.md) — DataAnnotations, custom hooks, nested validation
- [Configuration Binding](configuration-binding.md) — supported types, collections, nested configs
- [JSON Schema Emission](json-schema-emission.md) — opt into `appsettings.schema.json` for IDE IntelliSense
- [AOT and Trimming](aot-and-trimming.md) — how the reflection-free pipeline works
- [Diagnostics Reference](diagnostics.md) — all CB0001–CB0011 diagnostics with examples
