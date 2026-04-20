# ConfigBoundNET

**Compile-time validated configuration for .NET.**

Annotate a `partial record` with `[ConfigSection("…")]` and ConfigBoundNET generates a validator, a DI extension method, and build-time diagnostics for free. Configuration bugs that used to surface in production — typos in section names, missing required keys, mistyped property names — now fail at build time or at host startup, never at 3 a.m.

---

## The problem

The idiomatic ASP.NET Core configuration pattern is stringly-typed and silently lossy:

```csharp
var conn = builder.Configuration["Db:Conn"];                 // typo? nobody tells you
var timeout = builder.Configuration.GetValue<int>("Db:TO");  // wrong key? returns 0
```

Even the strongly-typed `IOptions<T>` pattern punts validation to runtime and requires boilerplate for every new section:

```csharp
services.Configure<DbConfig>(config.GetSection("Db"));
services.AddSingleton<IValidateOptions<DbConfig>, DbConfigValidator>();
// …and you still have to write DbConfigValidator by hand.
```

## The solution

Declare the shape once:

```csharp
using ConfigBoundNET;
using System.ComponentModel.DataAnnotations;

namespace MyApp;

[ConfigSection("Db")]
public partial record DbConfig
{
    public string Conn { get; init; } = default!;           // required (non-nullable)
    public int    CommandTimeoutSeconds { get; init; } = 30; // optional with default
    public string? ReplicaConn { get; init; }                // optional (nullable)

    [Range(1, 65535)]                                        // validated at startup
    public int Port { get; init; } = 5432;

    [RegularExpression(@"^https?://")]
    public string Endpoint { get; init; } = default!;
}
```

Register it in one line:

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddDbConfig(builder.Configuration)   // <- generated per-type
    .AddOptions<DbConfig>()
    .ValidateOnStart();
```

Or, if you have a handful of `[ConfigSection]` types and just want them all wired up at once:

```csharp
// Registers every [ConfigSection] type in the assembly whose section exists
// in config. Skipped types (e.g. nested-only types with throwaway section
// names) stay unregistered — call AddXxxConfig(config) explicitly if you
// need validation to fire against an absent section.
builder.Services.AddConfigBoundSections(builder.Configuration);
```

`AddConfigBoundSections` is additive: the per-type `AddXxxConfig` methods are still emitted and still compose with `AddOptions<T>().ValidateOnStart()`.

ConfigBoundNET generates everything else at build time:

- `DbConfig.SectionName` — a compile-time `const string` equal to `"Db"`.
- `DbConfig(IConfigurationSection)` — an AOT-safe, reflection-free constructor that reads values from config with explicit `TryParse` calls per property.
- `DbConfig.Validator` — an `IValidateOptions<DbConfig>` that null/whitespace-checks every required property **and** emits explicit checks for DataAnnotations (`[Range]`, `[RegularExpression]`, `[StringLength]`, etc.).
- `DbConfigServiceCollectionExtensions.AddDbConfig(IServiceCollection, IConfiguration)` — binds the section, registers the validator idempotently via `TryAddEnumerable`, and wires change-token propagation so `IOptionsMonitor<T>` reacts to reloads.

If the connection string is missing from `appsettings.json`, the host fails at startup with:

```
OptionsValidationException: [Db:Conn] is required but was null, empty, or whitespace.
```

...instead of the usual `NullReferenceException` buried three layers deep in your data access code.

---

## Install

ConfigBoundNET ships as a single analyzer package. There is no runtime dependency to add — the `[ConfigSection]` attribute is emitted into your own assembly at build time.

```xml
<ItemGroup>
  <PackageReference Include="ConfigBoundNET" Version="2.4.0" />
</ItemGroup>
```

You will also need the standard `IOptions<T>` binding packages, which are already transitive in most ASP.NET Core / Generic Host apps:

```xml
<PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="10.0.5" />
```

---

## How required fields are detected

ConfigBoundNET infers required-ness from the type system, so you almost never need extra attributes.

| Declaration                                   | Treated as | Rationale                                               |
| --------------------------------------------- | ---------- | ------------------------------------------------------- |
| `public string Conn { get; init; } = …;`      | Required   | Non-nullable reference type.                            |
| `public string? Conn { get; init; }`          | Optional   | Nullable reference type — explicitly opted out.         |
| `public required string Conn { get; init; }`  | Required   | C# 11 `required` modifier honored.                      |
| `public int Timeout { get; init; }`           | Not checked | Value types bind defaults; layer DataAnnotations on top for bounds checks. |

Required reference-type properties get a null check. `string` properties additionally get an `IsNullOrWhiteSpace` check — empty strings in config are almost always deployment mistakes.

---

## DataAnnotations validation

On top of the nullability-driven required-field checks, ConfigBoundNET scans for standard `System.ComponentModel.DataAnnotations` attributes and emits **explicit, reflection-free validation checks** for each one. No `Validator.TryValidateObject`, no reflection — just straight `if` statements in the generated `Validate` method.

```csharp
[ConfigSection("Api")]
public partial record ApiConfig
{
    [Range(1, 65535)]
    public int Port { get; init; } = 8080;

    [StringLength(200, MinimumLength = 5)]
    public string DisplayName { get; init; } = default!;

    [RegularExpression(@"^https?://")]
    public string Endpoint { get; init; } = default!;

    [Url]
    public string CallbackUrl { get; init; } = default!;

    [EmailAddress]
    public string AdminEmail { get; init; } = default!;
}
```

### Supported annotations

| Attribute | Applies to | Generated check |
| --- | --- | --- |
| `[Required]` | any | null/whitespace check (same as nullability; CB0009 warns if redundant) |
| `[Range(min, max)]` | numeric types | `if (options.X < min \|\| options.X > max)` |
| `[StringLength(max, MinimumLength = n)]` | `string` | `if (options.X.Length < n \|\| options.X.Length > max)` |
| `[MinLength(n)]` | `string` | `if (options.X.Length < n)` |
| `[MaxLength(n)]` | `string` | `if (options.X.Length > n)` |
| `[RegularExpression(pattern)]` | `string` | precompiled static `Regex` field + `IsMatch` |
| `[Url]` | `string` | `Uri.TryCreate(options.X, UriKind.Absolute, out _)` |
| `[EmailAddress]` | `string` | precompiled static `Regex` matching the BCL pattern |
| `[AllowedValues(...)]` | any (.NET 8+) | inline array + `Contains` |
| `[DeniedValues(...)]` | any (.NET 8+) | inline array + `Contains` (negated) |

All string checks are null-guarded so they don't mask the primary "required" error.

Misapplied annotations are caught at **build time** with dedicated diagnostics (CB0006–CB0009, see table below). Invalid regex patterns in `[RegularExpression]` are validated during compilation via `new Regex(pattern)` and produce CB0008 immediately.

---

## Custom validation hook

For cross-field rules that no single-property attribute can express, ConfigBoundNET emits **two** partial `ValidateCustom` methods on every annotated type. Implement whichever fits; unimplemented hooks are removed at compile time.

### Simple form

```csharp
[ConfigSection("Db")]
public partial record DbConfig
{
    public string? ConnString { get; init; }
    public string? ConnStringSecretRef { get; init; }

    partial void ValidateCustom(List<string> failures)
    {
        if (ConnString is null && ConnStringSecretRef is null)
            failures.Add("[Db] Either ConnString or ConnStringSecretRef must be set.");

        if (ConnString is not null && ConnStringSecretRef is not null)
            failures.Add("[Db] Set ConnString or ConnStringSecretRef, not both.");
    }
}
```

### Path-aware form (recommended for reusable types)

When the same type may be used standalone **and** as a nested or list-element config, the second overload hands you the full runtime configuration path so your failure messages stay correctly scoped regardless of where the type sits in the tree:

```csharp
[ConfigSection("__endpoint__")]  // section name only used when registered standalone
public partial record EndpointConfig
{
    public string? Host { get; init; }
    public string? Url { get; init; }

    partial void ValidateCustom(List<string> failures, string path)
    {
        if (Host is null && Url is null)
            failures.Add($"[{path}] Either Host or Url must be set.");
    }
}
```

Used standalone: `[__endpoint__] Either Host or Url must be set.` Used as `List<EndpointConfig>` under `ApiConfig("Api")`, element 2: `[Api:Endpoints:2] Either Host or Url must be set.`

Both hooks run **after** all generated checks (null/whitespace + DataAnnotations + nested validation), so you can safely read already-validated properties. Any hook you don't implement costs nothing at runtime — the C# compiler removes the call site entirely.

---

## Redacting sensitive properties

Mark a property with `[Sensitive]` to replace its value with `***` in the generated `ToString()` (records get a `PrintMembers` override; classes get a `ToString` override). Good for connection strings, API keys, OAuth tokens — anything you don't want dumped into logs:

```csharp
[ConfigSection("Db")]
public partial record DbConfig
{
    [Sensitive]
    public string Conn { get; init; } = default!;

    public int Port { get; init; } = 5432;
}

// logger.LogInformation("{Config}", config);   →  DbConfig { Conn = ***, Port = 5432 }
// config.ToString();                           →  DbConfig { Conn = ***, Port = 5432 }
```

Null values render as `null` so missing-config stays distinguishable from set-but-redacted. Value-type secrets (e.g. `[Sensitive] Guid Token`) always render as `***` — they can't be null at the CLR level.

The override is only emitted when at least one property on the type carries `[Sensitive]`. Types without it keep the compiler's default record `ToString` behaviour — no unwanted overrides, no surprises.

### Transparent across structured-logging frameworks

Redaction isn't limited to `ToString()`. The generator also emits an **explicit `IReadOnlyDictionary<string, object?>` implementation** on the type, so every major structured-logging framework and serializer reads redacted values through the interface contract instead of reflecting over properties:

| Framework / Serializer | Reads through the dictionary contract? |
|---|---|
| Serilog `{@Config}` | ✅ default destructurer renders `IEnumerable<KVP>` as `DictionaryValue` |
| MEL `JsonConsoleFormatter` | ✅ System.Text.Json picks `ReadOnlyDictionaryConverter<string, TValue>` |
| NLog `${json-encode}` | ✅ recognises `IReadOnlyDictionary` |
| `JsonSerializer.Serialize(config)` (STJ) | ✅ dictionary converter |
| OpenTelemetry JSON log exporters | ✅ via STJ |

All interface members are **explicitly implemented**, so the user's own API surface is untouched: `config.Conn` still returns the real value, `foreach (var kvp in config)` still fails to compile, and record equality / cloning / `GetHashCode` are unaffected. The interface is only reachable via a cast to `IReadOnlyDictionary<string, object?>` — which is what loggers and serializers do when they detect the interface.

Emitted code is 100% reflection-free: every value is a direct `this.X` read. The frameworks' one-time type-detection step (which does use reflection) happens at startup and doesn't touch per-instance values.

**Remaining gaps** (rare; documented for completeness): custom Serilog `IDestructuringPolicy` implementations that bypass interface detection, and exotic log layouts that reflect directly over properties without checking for `IDictionary` / `IEnumerable`. Users who've written those have already opted out of framework conventions.

---

## JSON schema emission for `appsettings.json`

ConfigBoundNET can drop a draft 2020-12 JSON Schema alongside your `appsettings.json` on every build. Reference it via `"$schema"` and your IDE gains IntelliSense, validation red-squiggles, and enum dropdowns — no runtime cost, no extra annotations.

The schema mirrors the full `[ConfigSection]` graph: property types, required-ness, `[Range]` bounds, `[StringLength]`/`[MinLength]`/`[MaxLength]`, `[RegularExpression]` patterns, `[Url]`/`[EmailAddress]` formats, `[AllowedValues]`/`[DeniedValues]`, enum member names, nested objects, collections, and `[Sensitive]` (emitted as `"writeOnly": true`).

### Emission model

Emission is always a two-step pipeline:

1. **The generator** produces `ConfigBoundNET.ConfigBoundJsonSchema.Json` — a `public const string` holding the full schema document. It ships in the same compilation as everything else the generator emits, so it's trivially reachable at runtime (e.g. `File.WriteAllText("schema.json", ConfigBoundJsonSchema.Json)` from a tool, or an `/schema` endpoint in dev).
2. **An MSBuild task** (shipped in the same NuGet as the generator) reads that const out of the just-built assembly via `MetadataLoadContext` and writes it to disk. This runs after `Build` and uses MSBuild `Inputs`/`Outputs` so a no-op rebuild leaves the file's mtime stable.

### Opt-in

The file-on-disk side is **off by default** so upgrades don't surprise anyone with a new tracked file in their repo. Flip it on per-project:

```xml
<PropertyGroup>
  <ConfigBoundEmitSchema>true</ConfigBoundEmitSchema>
  <!-- Optional — defaults to $(MSBuildProjectDirectory)\appsettings.schema.json -->
  <ConfigBoundSchemaOutputPath>$(MSBuildProjectDirectory)\config\appsettings.schema.json</ConfigBoundSchemaOutputPath>
</PropertyGroup>
```

Then point your `appsettings.json` at it:

```json
{
  "$schema": "./appsettings.schema.json",
  "Db": {
    "Conn": "Server=localhost;Database=App;Trusted_Connection=True;",
    "CommandTimeoutSeconds": 30
  }
}
```

Commit the `.schema.json` file alongside `appsettings.json` — it's deterministic output from your `[ConfigSection]` types, and checking it in means contributors get IntelliSense before their first local build.

### The generated schema (example)

For the `DbConfig` at the top of this README, `appsettings.schema.json` is roughly:

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "additionalProperties": true,
  "properties": {
    "Db": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "Conn": { "type": "string", "writeOnly": true, "description": "Sensitive — redacted in logs." },
        "CommandTimeoutSeconds": { "type": "integer" },
        "Port":     { "type": "integer", "minimum": 1, "maximum": 65535 },
        "Endpoint": { "type": "string", "pattern": "^https?://" }
      },
      "required": ["Conn", "Endpoint"]
    }
  }
}
```

`additionalProperties: true` at the root lets editors tolerate sections ConfigBoundNET doesn't own (`Logging`, `AllowedHosts`, etc.). Each `[ConfigSection]` object sets `additionalProperties: false` so typos inside the sections you *do* own surface as red squiggles.

### Caveats

- Cross-assembly nested `[ConfigSection]` types (the referenced type lives in another assembly the current compilation can't see) fall back to a permissive `{ "type": "object", "additionalProperties": true }` and raise **CB0011** (info-level) so you know which properties are degraded.
- `[RegularExpression]` patterns are passed through verbatim. JSON Schema speaks ECMA 262 regex; .NET regex is a slightly different dialect. Simple patterns (most real-world config validators) round-trip; exotic `.NET`-only constructs may behave differently in the editor than at runtime.

---

## Supported property types

The generator emits a **reflection-free** binder, so the set of property types it can read is fixed and explicit. Anything outside this list raises a `CB0010` warning at build time and is silently skipped at runtime (the property keeps its declared default).

| Category | Types |
| --- | --- |
| Strings | `string` |
| Integers | `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong` |
| Floating point | `float`, `double`, `decimal` |
| Boolean | `bool` |
| Time | `TimeSpan`, `DateTime`, `DateTimeOffset` |
| Identifiers | `Guid` |
| URLs | `Uri` (parsed as `UriKind.Absolute`) |
| Enums | any user-defined `enum` (parsed via `Enum.TryParse<T>`, case-insensitive) |
| Nested config | any other `[ConfigSection]`-annotated type |
| Nullable variants | `T?` for every value type above |

All numeric, date, and time parsing uses `CultureInfo.InvariantCulture` so config values are portable across locales.

Collections are fully supported:

| Collection type | Generated container |
| --- | --- |
| `T[]` | `List<T>` → `.ToArray()` |
| `List<T>`, `IList<T>`, `ICollection<T>`, `IEnumerable<T>`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>` | `new List<T>()` |
| `Dictionary<string, T>`, `IDictionary<string, T>`, `IReadOnlyDictionary<string, T>` | `new Dictionary<string, T>()` |
| `List<T>` / `T[]` / `IReadOnlyList<T>` (and other list-like shapes) where `T` is `[ConfigSection]`-annotated | iterates `GetChildren()` and calls `new T(child)` per element; each element is validated via its own generated `Validator` |
| `Dictionary<string, T>` / `IDictionary<string, T>` / `IReadOnlyDictionary<string, T>` where `T` is `[ConfigSection]`-annotated | iterates `GetChildren()` and builds `dict[child.Key] = new T(child)`; each value is validated by the element type's `Validator`, keyed by the config key (so failures read `[Api:Tenants:acme:Prop]`) |

Element types can be any scalar from the table above, or any `[ConfigSection]`-annotated type for the complex-element variant. If the config section is absent, user-declared defaults are preserved.

Element nullability annotations (`List<T?>`, `T?[]`, `IReadOnlyList<T?>`, `Dictionary<string, T?>`) are accepted but have no effect on binding — the generator never produces null elements or null dictionary values. The annotation is preserved in the emitted container type so the assignment type-checks under strict nullable; the validator defensively null-guards each element.

## Unsupported collections

These are explicitly **out of scope** for now and will remain `Unsupported` (CB0010):

| Type | Why |
|---|---|
| `HashSet<T>`, `SortedSet<T>` | IConfiguration doesn't distinguish sets from lists; semantically the user wants deduplication, but config arrays often don't guarantee uniqueness. Low demand. Add later if requested. |
| `Dictionary<TKey, T>` where TKey != `string` | IConfigurationSection child keys are always strings. Non-string-keyed dictionaries would require a parse step for the key itself and `IConfiguration` doesn't model that. |
| `T[][]`, `List<List<T>>`, nested collections | IConfiguration's flat key model (`Section:0:0`) technically supports these, but the code generation becomes deeply nested and the use case is rare. Not worth the complexity. |
| `ImmutableArray<T>`, `ImmutableList<T>`, `FrozenSet<T>` | Would require extra package references (`System.Collections.Immutable`) in the consumer. Support later if demanded. |
| `Queue<T>`, `Stack<T>`, `LinkedList<T>`, `ConcurrentBag<T>` | Exotic for config. No demand. |
| `ReadOnlySpan<T>`, `Memory<T>` | Can't be stored as properties; not applicable to IOptions binding. |


---

## What the generator emits

For the `DbConfig` above, ConfigBoundNET emits roughly this (fully qualified names elided for readability):

```csharp
// <auto-generated/>
partial record DbConfig
{
    public const string SectionName = "Db";

    // Reflection-free constructor: one explicit assignment per property,
    // invariant culture parsing, defaults preserved when keys are absent.
    public DbConfig(IConfigurationSection section)
    {
        if (section is null) throw new ArgumentNullException(nameof(section));

        var connRaw = section["Conn"];
        if (connRaw is not null) this.Conn = connRaw;

        var timeoutRaw = section["CommandTimeoutSeconds"];
        if (timeoutRaw is not null
            && int.TryParse(timeoutRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t))
        {
            this.CommandTimeoutSeconds = t;
        }
    }

    public sealed class Validator : IValidateOptions<DbConfig>
    {
        // Precompiled regex for [RegularExpression] — compiled once, not per call.
        private static readonly Regex _cb_Regex_Endpoint =
            new(@"^https?://", RegexOptions.Compiled);

        public ValidateOptionsResult Validate(string? name, DbConfig options)
        {
            if (options is null)
                return ValidateOptionsResult.Fail("DbConfig instance was null.");

            var failures = new List<string>();

            // Required-field checks (from nullability analysis):
            if (string.IsNullOrWhiteSpace(options.Conn))
                failures.Add("[Db:Conn] is required but was null, empty, or whitespace.");

            // DataAnnotations checks (from [Range], [RegularExpression], etc.):
            if (options.Port < 1 || options.Port > 65535)
                failures.Add("[Db:Port] must be between 1 and 65535.");

            if (options.Endpoint is not null && !_cb_Regex_Endpoint.IsMatch(options.Endpoint))
                failures.Add("[Db:Endpoint] does not match the required pattern.");

            return failures.Count > 0
                ? ValidateOptionsResult.Fail(failures)
                : ValidateOptionsResult.Success;
        }
    }
}

public static class DbConfigServiceCollectionExtensions
{
    public static IServiceCollection AddDbConfig(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(DbConfig.SectionName);

        services.AddOptions();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<DbConfig>, DbConfig.Validator>());

        // Replace the default reflection-based OptionsFactory with a shim
        // that calls the generated constructor — the entire point of the
        // AOT-friendly path. Standard IConfigureOptions / IPostConfigureOptions
        // / IValidateOptions still run as usual.
        services.Replace(ServiceDescriptor.Singleton<IOptionsFactory<DbConfig>>(sp =>
            new ConfigBoundOptionsFactory<DbConfig>(
                sp.GetServices<IConfigureOptions<DbConfig>>(),
                sp.GetServices<IPostConfigureOptions<DbConfig>>(),
                sp.GetServices<IValidateOptions<DbConfig>>(),
                _ => new DbConfig(section))));

        // IOptionsMonitor reload-on-change still works because we wire up the
        // change-token source explicitly.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IOptionsChangeTokenSource<DbConfig>>(
                new ConfigurationChangeTokenSource<DbConfig>(Options.DefaultName, section)));

        return services;
    }
}
```

### Additional emissions for `[Sensitive]`-bearing types

When at least one property on the type carries `[Sensitive]`, the generator *additionally* emits a redacted `PrintMembers` override (records) / `ToString` override (classes), and an explicit `IReadOnlyDictionary<string, object?>` implementation. Members are elided below for brevity — the shape is mechanical:

```csharp
// <auto-generated/>
partial record DbConfig : IReadOnlyDictionary<string, object?>
{
    // ... everything above unchanged ...

    // Redacted ToString for records (classes get an override of ToString()
    // directly). Reference-type / nullable-value sensitives distinguish
    // null vs populated; non-nullable value-type sensitives always render as "***".
    protected virtual bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Conn = ");
        builder.Append(this.Conn is null ? "null" : "***");
        builder.Append(", Port = ");
        builder.Append(this.Port);
        return true;
    }

    // IReadOnlyDictionary<string, object?> — all members explicit, so
    // config.Conn still works and foreach(kvp in config) still doesn't
    // compile. Loggers and serializers detect the interface and read
    // redacted values through the dictionary contract without reflecting
    // over the type.
    private static readonly string[] _cb_Keys = new[] { "Conn", "Port" };

    int IReadOnlyCollection<KeyValuePair<string, object?>>.Count => 2;

    object? IReadOnlyDictionary<string, object?>.this[string key] => key switch
    {
        "Conn" => this.Conn is null ? null : "***",
        "Port" => this.Port,
        _ => throw new KeyNotFoundException(key),
    };

    IEnumerable<string>  IReadOnlyDictionary<string, object?>.Keys   => _cb_Keys;
    IEnumerable<object?> IReadOnlyDictionary<string, object?>.Values { get { yield return this.Conn is null ? null : "***"; yield return this.Port; } }

    bool IReadOnlyDictionary<string, object?>.ContainsKey(string key) => key is "Conn" or "Port";

    bool IReadOnlyDictionary<string, object?>.TryGetValue(string key, out object? value) { /* switch, redacted per case */ }

    IEnumerator<KeyValuePair<string, object?>> IEnumerable<KeyValuePair<string, object?>>.GetEnumerator() { /* yield each pair */ }
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)this).GetEnumerator();
}
```

Nothing of this block is emitted when no property is marked `[Sensitive]` — types stay lean and keep the compiler's default record behaviour. See [Redacting sensitive properties](#redacting-sensitive-properties) above for the full coverage story across Serilog, MEL, NLog, STJ, and OpenTelemetry.

You can inspect the real output by setting `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>` on your project — see `examples/ConfigBoundNET.Example` for a working setup.

---

## Diagnostics

| ID     | Severity | Meaning                                                                      |
| ------ | -------- | ---------------------------------------------------------------------------- |
| CB0001 | Error    | Type decorated with `[ConfigSection]` is missing the `partial` modifier.     |
| CB0002 | Error    | `[ConfigSection("")]` section name is null, empty, or whitespace.            |
| CB0003 | Error    | `[ConfigSection]` applied to a nested type (only top-level types allowed).   |
| CB0004 | Warning  | Annotated type has no public writable properties — nothing to bind.          |
| CB0005 | Error    | `[ConfigSection]` applied to a struct or other unsupported type kind.        |
| CB0006 | Warning  | `[Range]` applied to a non-numeric property type — annotation ignored.       |
| CB0007 | Warning  | `[StringLength]`/`[MinLength]`/`[MaxLength]` on a non-string — ignored.     |
| CB0008 | Error    | `[RegularExpression]` pattern is not a valid .NET regex.                      |
| CB0009 | Info     | `[Required]` is redundant — property is already non-nullable.                |
| CB0010 | Warning  | Property type is outside the AOT-safe binding set; will be skipped.          |
| CB0011 | Info     | Nested `[ConfigSection]` type defined outside this compilation; JSON schema falls back to a permissive object shape for that property. |

All diagnostics fire at **build time**. CB0001–CB0003, CB0005, and CB0008 fail the build; the rest are advisory.

---

## FAQ

**Why a `partial record`?**
The generator extends your type with a nested `Validator` class and a `SectionName` constant. That requires the `partial` modifier. Any reference type works — `partial class` is fine too — but records are a natural fit for immutable config.

**Does this work with `IOptionsMonitor<T>` / reload-on-change?**
Yes. The generated DI extension registers `ConfigurationChangeTokenSource<T>` directly, so file edits, environment-variable changes, and any other reloadable `IConfigurationProvider` flow through to `IOptionsMonitor<T>` callbacks the way they do for hand-rolled options.

**Is the generated binder AOT- and trim-safe?**
Yes, and it is verified in CI. ConfigBoundNET emits an explicit, reflection-free constructor for every annotated type and replaces the default `OptionsFactory<T>` with a thin shim (`ConfigBoundOptionsFactory<T>`, annotated with `[DynamicallyAccessedMembers(PublicParameterlessConstructor)]`) that calls it. There is no `ConfigurationBinder.Bind`, no `Activator.CreateInstance` of user code, and no IL2026 / IL3050 / IL2091 warnings on properties whose types are in the supported set. Anything outside that set produces CB0010 at build time so you know up-front what won't be bound. The [`AOT smoke test`](.github/workflows/aot.yml) workflow runs a full `dotnet publish -p:PublishAot=true` against [`tests/ConfigBoundNET.AotTests`](tests/ConfigBoundNET.AotTests/) on every push and PR to `main`, so any regression in generated code's AOT-friendliness fails CI before it can ship.

**What about nested configuration types (a `DbConfig` that contains a `RetryPolicyConfig`)?**
Annotate the inner type with `[ConfigSection]` too. Both types get their own generated `(IConfigurationSection)` constructor and the outer one's binder simply recurses into it. You don't need a top-level `services.AddRetryPolicyConfig(...)` call — registering the outer type is enough; the inner one is wired up through the recursive constructor.

**Why does the attribute get stripped from my assembly metadata?**
`ConfigSectionAttribute` is decorated with `[Conditional("CONFIGBOUNDNET_KEEP_ATTRIBUTES")]`, which propagates to its usages. The source generator sees the attribute at build time through the syntax tree, but IL emission skips it unless you define that preprocessor symbol. Define it in your csproj if you need the attribute visible to runtime reflection.

**Why does `AddDbConfig` return `IServiceCollection` rather than `OptionsBuilder<T>`?**
So you can fluently chain other `services.AddX(...)` calls. If you want to tack extra validators onto the options pipeline, call `services.AddOptions<DbConfig>()` afterwards and chain from there (the generator's registration is idempotent).

**How does this compare to Microsoft's `[OptionsValidator]` source generator?**
Microsoft ships two built-in generators: `[OptionsValidator]` (generates validation from DataAnnotations) and `EnableConfigurationBindingGenerator` (generates AOT-safe binding). ConfigBoundNET replaces **both** with a single `[ConfigSection]` attribute and adds features neither provides: section name management (explicit or inferred), one-line DI extension methods, nullability-based required-field inference, custom cross-field validation hooks, collection binding, recursive nested validation, build-time diagnostics (CB0001-CB0010), and IDE code fixes. Both ConfigBoundNET and Microsoft's generator support `ErrorMessage` on attributes with `{0}`/`{1}`/`{2}` placeholders. See [Comparison with Microsoft Generators](docs/comparison-with-microsoft.md) for a full feature matrix.

---

## Repository layout

```
ConfigBoundNET/
├── src/ConfigBoundNET/              # The incremental source generator (ships as NuGet)
├── src/ConfigBoundNET/build/        # MSBuild .props/.targets packed into build/ of the NuGet
├── src/ConfigBoundNET.CodeFixes/    # IDE code-fix providers (separate assembly per RS1038)
├── src/ConfigBoundNET.Build/        # MSBuild task that writes appsettings.schema.json
├── tests/ConfigBoundNET.Tests/      # xUnit tests driving the generator directly
├── tests/ConfigBoundNET.AotTests/   # AOT smoke-test app (IsAotCompatible=true)
└── examples/ConfigBoundNET.Example/ # Minimal Generic Host app demonstrating end-to-end use
```

### Build & test

```bash
# Restore + build the whole solution (generator, tests, AOT smoke, example).
dotnet build ConfigBoundNET.sln

# 87 unit + integration + snapshot + cache tests.
dotnet test  tests/ConfigBoundNET.Tests/ConfigBoundNET.Tests.csproj
```

### Run the AOT smoke test

The AOT smoke test exercises every supported `BindingStrategy` end-to-end and asserts on the bound values. Two ways to run it:

```bash
# Fast — managed build only. <IsAotCompatible>true</IsAotCompatible> bundles
# the trim, AOT, and single-file analyzers; combined with TreatWarningsAsErrors,
# any IL2026 / IL3050 / IL2091 warning from generated code becomes a build error.
dotnet run --project tests/ConfigBoundNET.AotTests/ConfigBoundNET.AotTests.csproj

# Slow — full Native AOT publish. Catches anything the static analyzer can't
# see (e.g. closed-generic call sites only inspected during ILC). Requires the
# C++ AOT toolchain locally; on Linux it ships with the .NET SDK image.
dotnet publish tests/ConfigBoundNET.AotTests/ConfigBoundNET.AotTests.csproj -c Release
```

A successful run prints:

```
[AOT smoke] OK — every supported BindingStrategy round-tripped through the generated binder.
```

CI runs both forms on every push and PR — see [`.github/workflows/aot.yml`](.github/workflows/aot.yml).

### Run the example

```bash
cd examples/ConfigBoundNET.Example
dotnet run
```

Expected output:

```
[Db] Conn                   = Server=localhost;Database=App;Trusted_Connection=True;
[Db] CommandTimeoutSeconds  = 30
[Db] ReplicaConn            = (not set)

Validation passed — all cross-field rules satisfied.

ToString()   : DbConfig { Conn = ***, CommandTimeoutSeconds = 30, ReplicaConn =  }
JSON via STJ : {"Conn":"***","CommandTimeoutSeconds":30,"ReplicaConn":null}
```

The last two lines show `[Sensitive]` in action. Direct property access (the top three lines) still returns the raw value — your app needs it to actually open a connection. But when the config is rendered as a whole — `ToString()`, or serialized through any structured-logging destructurer / JSON serializer — `Conn` becomes `***`. See [Redacting sensitive properties](#redacting-sensitive-properties) above for how it works.

Try editing `appsettings.json` to remove the `Conn` value and re-run — the host now fails at startup with a precise, actionable error.

### Benchmarks

Performance comparisons against Microsoft's source-generated binder and validator live in [`benchmarks/`](benchmarks/).

```bash
# Run every benchmark (binding + validation):
dotnet run -c Release --project benchmarks/ConfigBoundNET.Benchmarks

# Only the binding comparisons:
dotnet run -c Release --project benchmarks/ConfigBoundNET.Benchmarks -- --filter '*Bind*'
```

See [`benchmarks/README.md`](benchmarks/README.md) for what's measured, how to confirm both Microsoft generators actually activated (verify the comparison is real), and how to read the results.

### Cloud configuration providers

ConfigBoundNET is provider-agnostic — it binds from any `IConfiguration`, so Azure App Configuration, Key Vault, AWS Parameter Store / Secrets Manager, GCP Secret Manager, and HashiCorp Vault all work out of the box. See [`docs/cloud-providers.md`](docs/cloud-providers.md) for the one-liner recipe per provider and a list of gotchas (path separators, reload behaviour, secure-string handling, `[Sensitive]` pairing).

---

## Roadmap

ConfigBoundNET is at **v2.0.0** and feature-complete for its core remit: declare a `partial record`, get an AOT-safe binder, validator, and one-line DI extension with build-time diagnostics. The items below are candidate extensions, ordered by value-to-effort ratio — not commitments.

### Planned

- [x] **`List<[ConfigSection]>` — complex nested collections.** ✅ `List<T>` / `T[]` / `IReadOnlyList<T>` (and the other list-like shapes) bind via `section.GetChildren()` + `new T(child)` per element when `T` is itself `[ConfigSection]`-annotated. Each element is validated through its own generated `Validator`, and failures are merged into the parent's result with an index-tagged named-options key. `[MinLength]`/`[MaxLength]` work on these collections via `.Count` / `.Length`. `Dictionary<string, ComplexType>` remains a follow-up.
- [x] **JSON schema emission for `appsettings.json`.** ✅ The generator always emits `ConfigBoundNET.ConfigBoundJsonSchema.Json` (a `const string` with the draft 2020-12 schema covering every `[ConfigSection]`, including nested graphs, enum members, DataAnnotations bounds, and `[Sensitive]` → `writeOnly`). A shipped MSBuild task writes it to disk at build time when `<ConfigBoundEmitSchema>true</ConfigBoundEmitSchema>` is set, so editors pick it up via `"$schema"` in `appsettings.json`. See [JSON schema emission for `appsettings.json`](#json-schema-emission-for-appsettingsjson).
- [x] **`[Sensitive]` attribute + redacted `ToString` + transparent structured-logging redaction.** ✅ Properties marked `[Sensitive]` render as `***` in the generated `ToString()` (records get a `PrintMembers` override; classes get a `ToString` override). Types with any `[Sensitive]` property also explicitly implement `IReadOnlyDictionary<string, object?>`, so Serilog `{@X}`, MEL's `JsonConsoleFormatter`, NLog, OpenTelemetry JSON exporters, and `JsonSerializer.Serialize` all read redacted values through the interface contract — **no reflection**, transparent to callers. Explicit interface members keep the user's API surface unchanged. See [Redacting sensitive properties](#redacting-sensitive-properties) in this README.
- [ ] **Analyzer for stringly-typed config access.** When a `[ConfigSection("Db")]` exists, flag `configuration["Db:Conn"]` / `configuration.GetValue<T>("Db:…")` and suggest injecting `IOptions<DbConfig>` instead. Turns the generator into a migration tool for existing codebases.
- [x] **`AddConfigBoundSections(IConfiguration)` aggregate registration.** ✅ One assembly-wide extension in the `ConfigBoundNET` namespace calls every per-type `AddXxxConfig` in alphabetical order, each wrapped in a `ConfigurationExtensions.Exists(section)` gate so types whose section is absent (typically nested-only types with throwaway section names) are silently skipped. Additive: per-type extensions are still emitted and still compose with `AddOptions<T>().ValidateOnStart()`. Users who need validation to fire against an absent section call the per-type `AddXxxConfig` directly.
- [ ] **Propagate XML doc comments.** Forward `///` summaries from the user's config properties onto the generated `Add{Name}Config` method and any public generated members, so IntelliSense keeps working.
- [ ] **`partial void OnBound(IConfigurationSection section)` hook.** Called after construction and before validation; lets users derive computed properties or normalize values without touching the generated constructor. Same zero-cost pattern as `ValidateCustom`.
- [ ] **SourceLink + deterministic builds + `.snupkg`** so users get source debugging on nuget.org.
- [ ] **`CHANGELOG.md`** wired into the package via `PackageReleaseNotes`.

### Out of scope

- **Async validation.** `IValidateOptions<T>` is sync; async checks (e.g. probing a DB connection) belong in a hosted startup task, not the validator.
- **Custom configuration providers / new sources.** Orthogonal concern — ConfigBoundNET binds whatever `IConfiguration` already exposes.
- **Struct support.** `record struct` configs are an anti-pattern with `IOptions<T>` (the framework hands out copies). Rejected by CB0005; intentionally left that way.
- **Immutable collection types** (`ImmutableArray<T>`, `FrozenSet<T>`). Would require shipping `System.Collections.Immutable` as a transitive dependency to every consumer. Reconsider if demand appears.
- **Custom binding plugins.** A user-extensible type-handler registry would force reflection back into the pipeline and defeat the AOT guarantee.

---

## Contributing

Issues and PRs welcome. Please make sure `dotnet test` passes and that any new diagnostic IDs are listed in `src/ConfigBoundNET/AnalyzerReleases.Unshipped.md`.

## License

GPL3. See `LICENSE` for details.
