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

namespace MyApp;

[ConfigSection("Db")]
public partial record DbConfig
{
    public string Conn { get; init; } = default!;           // required (non-nullable)
    public int    CommandTimeoutSeconds { get; init; } = 30; // optional with default
    public string? ReplicaConn { get; init; }                // optional (nullable)
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
- `DbConfig.Validator` — an `IValidateOptions<DbConfig>` that null/whitespace-checks every required property.
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
  <PackageReference Include="ConfigBoundNET" Version="0.1.0" />
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

Collections (`T[]`, `List<T>`, `Dictionary<string, T>`) are not yet supported and produce CB0010. They are tracked under Tier 2 of the roadmap below.

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
        public ValidateOptionsResult Validate(string? name, DbConfig options)
        {
            if (options is null)
                return ValidateOptionsResult.Fail("DbConfig instance was null.");

            var failures = new List<string>();

            if (string.IsNullOrWhiteSpace(options.Conn))
                failures.Add("[Db:Conn] is required but was null, empty, or whitespace.");

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

| ID     | Severity | Meaning                                                                   |
| ------ | -------- | ------------------------------------------------------------------------- |
| CB0001 | Error    | Type decorated with `[ConfigSection]` is missing the `partial` modifier.  |
| CB0002 | Error    | `[ConfigSection("")]` section name is null, empty, or whitespace.         |
| CB0003 | Error    | `[ConfigSection]` applied to a nested type (only top-level types allowed). |
| CB0004 | Warning  | Annotated type has no public writable properties — nothing to bind.       |
| CB0005 | Error    | `[ConfigSection]` applied to a struct or other unsupported type kind.     |
| CB0010 | Warning  | Property type is outside the AOT-safe binding set; will be skipped.       |

All diagnostics fire at **build time**. CB0001–CB0003 and CB0005 fail the build; CB0004 and CB0010 are advisory.

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

---

## Repository layout

```
ConfigBoundNET/
├── src/ConfigBoundNET/              # The incremental source generator (ships as NuGet)
├── tests/ConfigBoundNET.Tests/      # xUnit tests driving the generator directly
├── tests/ConfigBoundNET.AotTests/   # AOT smoke-test app (IsAotCompatible=true)
└── examples/ConfigBoundNET.Example/ # Minimal Generic Host app demonstrating end-to-end use
```

### Build & test

```bash
# Restore + build the whole solution (generator, tests, AOT smoke, example).
dotnet build ConfigBoundNET.sln

# 20 unit + integration tests covering every BindingStrategy.
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
```

Try editing `appsettings.json` to remove the `Conn` value and re-run — the host now fails at startup with a precise, actionable error.

---

## Roadmap

ConfigBoundNET is currently at **0.1**. The pieces below are tracked toward 1.0; PRs are welcome on any of them.

### Tier 1 — needed before 1.0

- [ ] **DataAnnotations validation.** Honor `[Range]`, `[StringLength]`, `[RegularExpression]`, `[Url]`, `[MinLength]`, `[EmailAddress]`, etc. Without this, anyone with a `Port { get; init; }` has to write a hand-rolled validator on the side, defeating the point of the library.
- [x] **Reflection-free binding (AOT support).** ✅ The generator emits an explicit `(IConfigurationSection)` constructor on every annotated type and registers a `ConfigBoundOptionsFactory<T>` shim that calls it, replacing `ConfigurationBinder.Bind` entirely. The pipeline is now trim- and Native-AOT-friendly for the supported type set listed above.
- [x] **AOT smoke-test workflow.** ✅ [`tests/ConfigBoundNET.AotTests`](tests/ConfigBoundNET.AotTests/) is a console app with `<IsAotCompatible>true</IsAotCompatible>` that exercises every `BindingStrategy` and asserts on the bound values. [`.github/workflows/aot.yml`](.github/workflows/aot.yml) runs the static analyzer build on every push and a full `dotnet publish` Native AOT compile on Linux x64 right after, executing the resulting native binary to confirm the round-trip. The smoke test caught and fixed one real defect during initial setup (an `IL2091` from the missing `[DynamicallyAccessedMembers]` annotation on `ConfigBoundOptionsFactory<T>`), which is exactly the kind of regression CI is now guarding against.
- [ ] **Custom validation hook.** Generate a `partial void ValidateCustom(List<string> failures)` so users can express cross-field rules ("`ConnString` XOR `ConnStringSecretRef` must be set") without writing a separate `IValidateOptions<T>`.
- [ ] **CodeFix providers** for CB0001 ("Add `partial` modifier") and CB0002 ("Use type name as section name"). One-click fixes inside the IDE.
- [ ] **CI workflow** (`.github/workflows/ci.yml`): build, test, pack, attach the `.nupkg` as an artifact, publish to NuGet on tag push. Independent of the existing AOT workflow above; this one is the release pipeline.

### Tier 2 — quality-of-life

- [ ] **`[ConfigSection]` with no argument** infers the section name from the type name (`DbConfig` → `"Db"`, stripping trailing `Config` / `Options` / `Settings`).
- [ ] **Nested config types.** If `DbConfig.Retry` is a non-nullable `RetryConfig`, recurse the null/required checks into it. Currently complex properties are ignored.
- [ ] **Collection support.** Detect `List<T>`, `T[]`, `Dictionary<string, T>` and at minimum require non-empty when non-nullable.
- [ ] **Snapshot tests with `Verify.SourceGenerators`** so refactors of the emitter cannot silently change generated output.
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
