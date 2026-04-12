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
    .AddDbConfig(builder.Configuration)   // <- generated
    .AddOptions<DbConfig>()
    .ValidateOnStart();
```

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
  <PackageReference Include="ConfigBoundNET" Version="1.0.0" />
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

For cross-field rules that no single-property attribute can express, ConfigBoundNET emits a `partial void ValidateCustom` method on every annotated type. Implement it to add your own checks:

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

The hook runs **after** all generated checks (null/whitespace + DataAnnotations), so you can safely read already-validated properties. If you don't implement it, the C# compiler removes the call site entirely — zero runtime cost.

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

Element types can be any scalar from the table above. If the config section is absent, user-declared defaults are preserved.

## Unsupported collections

These are explicitly **out of scope** for now and will remain `Unsupported` (CB0010):

| Type | Why |
|---|---|
| `HashSet<T>`, `SortedSet<T>` | IConfiguration doesn't distinguish sets from lists; semantically the user wants deduplication, but config arrays often don't guarantee uniqueness. Low demand. Add later if requested. |
| `Dictionary<TKey, T>` where TKey != `string` | IConfigurationSection child keys are always strings. Non-string-keyed dictionaries would require a parse step for the key itself and `IConfiguration` doesn't model that. |
| `List<ComplexType>` where ComplexType is a `[ConfigSection]`-annotated record | Each array element would be a sub-section with its own key-value children. Doable (the child is an `IConfigurationSection` itself, and we'd call `new ComplexType(child)`), but it adds recursive complexity. **Tracked as a follow-up.** |
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
Microsoft ships two built-in generators: `[OptionsValidator]` (generates validation from DataAnnotations) and `EnableConfigurationBindingGenerator` (generates AOT-safe binding). ConfigBoundNET replaces **both** with a single `[ConfigSection]` attribute and adds features neither provides: section name management (explicit or inferred), one-line DI extension methods, nullability-based required-field inference, custom cross-field validation hooks, collection binding, recursive nested validation, build-time diagnostics (CB0001-CB0010), and IDE code fixes. See [Comparison with Microsoft Generators](docs/comparison-with-microsoft.md) for a full feature matrix.

---

## Repository layout

```
ConfigBoundNET/
├── src/ConfigBoundNET/              # The incremental source generator (ships as NuGet)
├── src/ConfigBoundNET.CodeFixes/    # IDE code-fix providers (separate assembly per RS1038)
├── tests/ConfigBoundNET.Tests/      # xUnit tests driving the generator directly
├── tests/ConfigBoundNET.AotTests/   # AOT smoke-test app (IsAotCompatible=true)
└── examples/ConfigBoundNET.Example/ # Minimal Generic Host app demonstrating end-to-end use
```

### Build & test

```bash
# Restore + build the whole solution (generator, tests, AOT smoke, example).
dotnet build ConfigBoundNET.sln

# 77 unit + integration + snapshot tests.
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
```

Try editing `appsettings.json` to remove the `Conn` value and re-run — the host now fails at startup with a precise, actionable error.

---

## Roadmap

ConfigBoundNET is currently at **v1.0.0**. The pieces below are tracked toward next major versions.

### Tier 1 — needed before 1.0

- [x] **DataAnnotations validation.** ✅ The generator scans for `[Range]`, `[StringLength]`, `[MinLength]`, `[MaxLength]`, `[RegularExpression]`, `[Url]`, `[EmailAddress]`, `[Required]`, `[AllowedValues]`, and `[DeniedValues]` and emits explicit, reflection-free validation checks. Misapplied annotations (e.g. `[Range]` on a string) are caught at build time with CB0006–CB0009 diagnostics. Regex patterns are validated at compile time. See the DataAnnotations section above.
- [x] **Reflection-free binding (AOT support).** ✅ The generator emits an explicit `(IConfigurationSection)` constructor on every annotated type and registers a `ConfigBoundOptionsFactory<T>` shim that calls it, replacing `ConfigurationBinder.Bind` entirely. The pipeline is now trim- and Native-AOT-friendly for the supported type set listed above.
- [x] **AOT smoke-test workflow.** ✅ [`tests/ConfigBoundNET.AotTests`](tests/ConfigBoundNET.AotTests/) is a console app with `<IsAotCompatible>true</IsAotCompatible>` that exercises every `BindingStrategy` and asserts on the bound values. [`.github/workflows/aot.yml`](.github/workflows/aot.yml) runs the static analyzer build on every push and a full `dotnet publish` Native AOT compile on Linux x64 right after, executing the resulting native binary to confirm the round-trip.
- [x] **Custom validation hook.** ✅ Every annotated type gets a `partial void ValidateCustom(List<string> failures)` declaration. Implement it on your config type to add cross-field rules; leave it unimplemented for zero cost. Called after all generated checks. See the custom validation hook section above.
- [x] **CodeFix providers.** ✅ Five one-click IDE lightbulb fixes: CB0001 ("Add `partial` modifier"), CB0002 ("Use `{Inferred}` as section name" — strips `Config`/`Options`/`Settings`/`Configuration` suffix), CB0003 ("Move type to namespace scope"), CB0005 ("Change to class"), CB0009 ("Remove redundant `[Required]`").
- [x] **CI workflow.** ✅ [`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs on every push/PR: restore, build, test (53 tests), AOT smoke, example run, pack `.nupkg` (uploaded as artifact). On `v*` tag pushes, a second job publishes to NuGet.org and creates a GitHub Release with auto-generated release notes. Version is derived from the tag name (`v0.2.0` -> `Version=0.2.0`).

### Tier 2 — quality-of-life

- [x] **`[ConfigSection]` with no argument.** ✅ `[ConfigSection] partial record DbConfig` infers `"Db"` by stripping `Config`/`Options`/`Settings`/`Configuration` suffixes. Explicit `[ConfigSection("")]` still errors with CB0002 (the code fix offers to fill in the inferred name).
- [x] **Nested config validation.** ✅ The parent's `Validator.Validate()` now recursively calls each nested `[ConfigSection]` type's own `Validator`, merging any failures into the parent's result. Required-field checks, DataAnnotations, and custom hooks on nested types all fire as part of the parent's validation pass.
- [x] **Collection support.** ✅ `T[]`, `List<T>`, `IList<T>`, `ICollection<T>`, `IEnumerable<T>`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>`, `Dictionary<string, T>`, `IDictionary<string, T>`, and `IReadOnlyDictionary<string, T>` are all supported. Elements can be any scalar type. Absent sections preserve user-declared defaults. `[MinLength]`/`[MaxLength]` work on collections (checking `.Count`/`.Length`). CB0007 relaxed to allow length attributes on collections.
- [x] **Snapshot tests with `Verify.SourceGenerators`.** ✅ 14 scenarios pinning the exact generated output via 44 `.verified.cs` files under `tests/ConfigBoundNET.Tests/Snapshots/`. Covers: basic record, parameterless attribute, class (non-record), global namespace, all primitive types, enums, nullable/optional, nested config, Range + StringLength, RegularExpression, Url + EmailAddress, MinLength + MaxLength, multiple annotations on one property, and the empty-type ValidateCustom hook. Any emitter change surfaces as a diff that must be explicitly accepted.
- [ ] **Generator perf test** asserting incremental cache hits via `GeneratorDriver.GetRunResult().Results[0].TrackedSteps` — protects against accidentally putting non-equatable types in the pipeline.
- [ ] **SourceLink + deterministic builds + `.snupkg`** so users get source debugging on nuget.org.
- [ ] **`CHANGELOG.md`** wired into the package via `PackageReleaseNotes`.

### Out of scope (probably)

- **Async validation.** `IValidateOptions<T>` is sync; async checks (e.g. probing a DB connection) belong in a hosted startup task.
- **Custom configuration providers / new sources.** Orthogonal concern — ConfigBoundNET binds whatever `IConfiguration` already exposes.
- **Struct support.** `record struct` configs are an anti-pattern with `IOptions<T>` (the framework hands out copies). Already rejected by CB0005; intentionally left that way.

---

## Contributing

Issues and PRs welcome. Please make sure `dotnet test` passes and that any new diagnostic IDs are listed in `src/ConfigBoundNET/AnalyzerReleases.Unshipped.md`.

## License

GPL3. See `LICENSE` for details.
