# Cloud configuration providers

ConfigBoundNET is **provider-agnostic**. It reads through the stock `IConfiguration` / `IConfigurationSection` APIs (`section["Key"]`, `section.GetChildren()`, `section.GetSection(...)`), so any package that plugs into `IConfigurationBuilder` works with zero special integration. No "ConfigBoundNET.AWS" or "ConfigBoundNET.Azure" adapter packages exist — none are needed.

This page lists the one-liner recipe for every major `IConfigurationProvider` and the gotchas worth knowing.

---

## Azure

### Azure App Configuration

```csharp
using ConfigBoundNET;

builder.Configuration.AddAzureAppConfiguration(connectionString);

builder.Services.AddConfigBoundSections(builder.Configuration);
```

Package: [`Microsoft.Extensions.Configuration.AzureAppConfiguration`](https://www.nuget.org/packages/Microsoft.Extensions.Configuration.AzureAppConfiguration).

**Push-reload works automatically.** The provider surfaces config changes through the standard `IConfigurationProvider.GetReloadToken()` channel, which ConfigBoundNET's generated DI extension already wires into `IOptionsMonitor<T>` via `ConfigurationChangeTokenSource<T>`. No extra code on the ConfigBoundNET side.

### Azure Key Vault

```csharp
using Azure.Identity;
using ConfigBoundNET;

builder.Configuration.AddAzureKeyVault(
    new Uri("https://my-vault.vault.azure.net/"),
    new DefaultAzureCredential());

builder.Services.AddConfigBoundSections(builder.Configuration);
```

Package: [`Azure.Extensions.AspNetCore.Configuration.Secrets`](https://www.nuget.org/packages/Azure.Extensions.AspNetCore.Configuration.Secrets).

**Managed identity works out of the box** with `DefaultAzureCredential` on Azure-hosted workloads (App Service, AKS, VMs, Container Apps).

**Key mapping.** Key Vault uses `--` as the section delimiter in secret names. Both Microsoft's provider and ConfigBoundNET translate that to the `IConfiguration` colon delimiter: a secret named `Db--Conn` maps to `Db:Conn`, so `[ConfigSection("Db")] record DbConfig { string Conn }` binds from it without any generator change.

**JSON-valued secrets** are a common Key Vault pattern — a single secret holds `{"Conn": "...", "Port": 5432}`. Key Vault's provider reads the whole value as a string, so if you want the shape parsed:

```csharp
builder.Configuration
    .AddAzureKeyVault(vaultUri, credential)
    .AddJsonStream(loadedJsonStream);  // layer the JSON on top
```

Or restructure the secret into per-leaf entries (`Db--Conn`, `Db--Port`) so Key Vault's native mapping delivers them straight to `section["Key"]`.

---

## AWS

### AWS Systems Manager Parameter Store

```csharp
using ConfigBoundNET;

builder.Configuration.AddSystemsManager("/myapp/");

builder.Services.AddConfigBoundSections(builder.Configuration);
```

Package: [`Amazon.Extensions.Configuration.SystemsManager`](https://www.nuget.org/packages/Amazon.Extensions.Configuration.SystemsManager).

**Path mapping.** The AWS SDK translates parameters under `/myapp/` into `IConfiguration` keys by stripping the prefix and swapping `/` for `:`. So `/myapp/Db/Conn` maps to `Db:Conn`, which binds straight into `[ConfigSection("Db")] record DbConfig { string Conn }`. Nothing to configure on the ConfigBoundNET side.

**Reload via polling.** The SystemsManager provider polls on a configurable interval; reload tokens flow through `ConfigurationChangeTokenSource<T>` and reach `IOptionsMonitor<T>` listeners the same way as any file-based provider.

**`SecureString` parameter type** (KMS-encrypted) is decrypted by the AWS SDK before values reach `IConfiguration`. ConfigBoundNET sees plain strings and binds them normally. Combine with `[Sensitive]` on the receiving property for log-redaction hygiene:

```csharp
[ConfigSection("Db")]
public partial record DbConfig
{
    [Sensitive]                         // redacts in ToString()
    public string Conn { get; init; } = default!;
}
```

### AWS Secrets Manager

```csharp
using ConfigBoundNET;

builder.Configuration.AddSecretsManager(region: RegionEndpoint.USEast1);

builder.Services.AddConfigBoundSections(builder.Configuration);
```

Package: [`Kralizek.Extensions.Configuration.AWSSecretsManager`](https://www.nuget.org/packages/Kralizek.Extensions.Configuration.AWSSecretsManager) (community — the official AWS SDK doesn't ship a first-party Secrets Manager `IConfigurationProvider`).

**JSON-valued secrets** are the norm in Secrets Manager. The community provider auto-flattens JSON secrets into colon-delimited keys, so a secret named `myapp/db` containing `{"Conn": "...", "Port": 5432}` maps to `myapp:db:Conn` and `myapp:db:Port`. Set the prefix/key-mapping options on the builder to line up with your `[ConfigSection]` names.

### AWS AppConfig

```csharp
using ConfigBoundNET;

builder.Configuration.AddAppConfig(
    applicationId: "myapp",
    environmentId: "prod",
    configProfileId: "default");

builder.Services.AddConfigBoundSections(builder.Configuration);
```

Package: [`Amazon.Extensions.Configuration.AppConfig`](https://www.nuget.org/packages/Amazon.Extensions.Configuration.AppConfig).

**Deployment-gated reloads.** AppConfig's rollout strategy (gradual / canary) flows through the standard reload-token mechanism.

---

## Google Cloud

### GCP Secret Manager

```csharp
using ConfigBoundNET;

builder.Configuration.AddGoogleSecretManager(projectId: "my-gcp-project");

builder.Services.AddConfigBoundSections(builder.Configuration);
```

Package options vary — [`Google.Cloud.SecretManager.V1`](https://www.nuget.org/packages/Google.Cloud.SecretManager.V1) provides the client; community wrappers (e.g. `GoogleCloudPlatform.Extensions.Configuration.SecretManager`) add the `IConfigurationProvider` integration.

**Workload Identity** authentication (when running on GKE) is handled by the Google Cloud SDK automatically; no extra work needed to reach ConfigBoundNET.

---

## HashiCorp Vault

```csharp
using ConfigBoundNET;

builder.Configuration.AddVaultConfiguration(
    () => new VaultOptions(vaultAddress, tokenOrRole),
    basePath: "myapp",
    mountPoint: "secret");

builder.Services.AddConfigBoundSections(builder.Configuration);
```

Package: [`VaultSharp.Extensions.Configuration`](https://www.nuget.org/packages/VaultSharp.Extensions.Configuration).

**Lease renewal.** For dynamic secrets with short TTLs, Vault's reload-token mechanism triggers re-fetching; `IOptionsMonitor<T>` sees the new values without any extra configuration on the binding side.

---

## Gotchas

### String-only at the provider boundary

Every cloud provider returns every value as a string. ConfigBoundNET's per-property `TryParse` path handles the conversion to `int` / `TimeSpan` / `Guid` / `DateTime(Offset)` / `Uri` / enums under `CultureInfo.InvariantCulture`. Nothing provider-specific required — `"00:00:30"` becomes a `TimeSpan` regardless of whether it came from `appsettings.json`, Parameter Store, or Key Vault.

### Reload-on-change works automatically

Our generated `AddXxxConfig` / `AddConfigBoundSections` extensions register `ConfigurationChangeTokenSource<T>` against the bound section. Every provider that raises reload tokens — Azure App Configuration push, Parameter Store polling, Vault lease renewal, Key Vault rotation (with provider-specific refresh config) — flows through to your `IOptionsMonitor<T>` callbacks the way it does for `appsettings.json`.

### Provider keys vs `[ConfigSection]` names

Cloud providers use native key shapes: `/` in Parameter Store, `--` in Key Vault, dotted paths in some. Every .NET provider translates its native shape to the colon-delimited `IConfiguration` key format. Your `[ConfigSection("Db")]` type binds from `Db:*` keys regardless of how the storage backend actually represents them — match your section name to the `:`-delimited form and the provider handles the rest.

### Compose providers; don't replace

Apps typically bind from a union: `appsettings.json` (defaults) + env vars (overrides) + Key Vault (secrets) + Parameter Store (runtime flags). `IConfigurationBuilder` is additive; later providers override earlier ones for matching keys. ConfigBoundNET sees the merged view and binds from whatever wins.

```csharp
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{env}.json", optional: true)
    .AddEnvironmentVariables("MYAPP_")
    .AddAzureKeyVault(vaultUri, credential)
    .AddSystemsManager("/myapp/");

builder.Services.AddConfigBoundSections(builder.Configuration);
```

### Secrets in logs

Use [`[Sensitive]`](../README.md#redacting-sensitive-properties) on properties that hold cloud secrets. Redaction flows through both `ToString()` **and** structured-logging destructurers automatically — types with any `[Sensitive]` property also explicitly implement `IReadOnlyDictionary<string, object?>`, which Serilog, MEL's `JsonConsoleFormatter`, NLog's JSON layouts, and `System.Text.Json` all pick up as the dictionary shape.

```csharp
[ConfigSection("Db")]
public partial record DbConfig
{
    [Sensitive]
    public string Conn { get; init; } = default!;      // comes from Key Vault / Parameter Store SecureString
}

logger.LogInformation("{@Config}", config);
// Serilog / MEL JSON / NLog json-encode → { "Config": { "Conn": "***", ... } }
```

No logger-specific packages, no custom destructuring policies, no `JsonConverter<T>` — the interface is the contract every major framework agrees on. A very small set of custom destructurers that reflect over properties directly (rather than checking for `IDictionary` / `IEnumerable`) bypass this; those need logger-level policies. See the main README for the full coverage table.

### `AddConfigBoundSections`' `.Exists()` gate

The aggregate extension wraps each `AddXxxConfig` in a `ConfigurationExtensions.Exists(section)` gate, so a `[ConfigSection("Api")]` type whose `Api` section isn't present in the merged `IConfiguration` is silently skipped. This matters with cloud providers: if Key Vault is unreachable at startup (or the vault URI is wrong and you haven't caught it yet), sections that would have come from it fall back to whatever other providers supplied — or to nothing. Use the per-type `services.AddXxxConfig(configuration)` explicitly if you want validation to fire even when a cloud-sourced section is absent.
