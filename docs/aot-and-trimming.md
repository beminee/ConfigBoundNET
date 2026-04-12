# AOT and Trimming

ConfigBoundNET is designed from the ground up to be **fully AOT- and trim-safe**. Every code path in the generated output avoids reflection and runtime code generation, and this is verified in CI on every push.

## The problem with standard options binding

The stock `ConfigurationBinder.Bind` and `services.Configure<T>(IConfiguration)` use reflection internally:

- `Activator.CreateInstance<T>()` to construct the options instance
- Property reflection to enumerate and set values
- `TypeConverter` and `Convert.ChangeType` for value parsing

All of these are annotated with `[RequiresUnreferencedCode]` and/or `[RequiresDynamicCode]`, which means they produce IL2026/IL3050 warnings under `<IsAotCompatible>true</IsAotCompatible>` and may fail at runtime under Native AOT.

## How ConfigBoundNET avoids reflection

### Binding: generated constructor

Instead of `ConfigurationBinder.Bind`, the generator emits a constructor on each annotated type:

```csharp
public DbConfig(IConfigurationSection section)
{
    var connRaw = section["Conn"];
    if (connRaw is not null) this.Conn = connRaw;

    var portRaw = section["Port"];
    if (portRaw is not null && int.TryParse(portRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        this.Port = port;
}
```

Every property assignment is an explicit, statically-compiled C# statement. No `PropertyInfo.SetValue`, no reflection.

The constructor lives on the partial type itself (not a static method), which is the only way to write to `init`-only properties from code — C# permits `init` writes from constructors and object initializers, but not from static methods.

### Factory replacement: ConfigBoundOptionsFactory&lt;T&gt;

The standard `OptionsFactory<T>` calls `Activator.CreateInstance<T>()` to create the options instance. ConfigBoundNET replaces it with a `ConfigBoundOptionsFactory<T>` that overrides `CreateInstance`:

```csharp
internal sealed class ConfigBoundOptionsFactory<T> : OptionsFactory<T> where T : class
{
    private readonly Func<string, T> _instanceFactory;

    protected override T CreateInstance(string name) => _instanceFactory(name);
}
```

The `_instanceFactory` delegate calls `new DbConfig(section)` — our generated constructor. The base `OptionsFactory<T>.Create` still runs all `IConfigureOptions<T>`, `IPostConfigureOptions<T>`, and `IValidateOptions<T>` passes, so the standard pipeline is preserved.

### DynamicallyAccessedMembers annotation

`OptionsFactory<TOptions>` constrains its type parameter with `[DynamicallyAccessedMembers(PublicParameterlessConstructor)]`. The shim propagates this annotation:

```csharp
internal sealed class ConfigBoundOptionsFactory<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T
> : OptionsFactory<T> where T : class
```

Without this annotation, the trim analyzer raises IL2091 at every registration site. This was caught and fixed by the AOT smoke test during initial development.

### Validation: explicit if-statements

The generated `Validator.Validate` method uses plain `if` statements for every check — no `Validator.TryValidateObject`, no attribute reflection. Regex patterns are precompiled as `private static readonly Regex` fields on the Validator class.

### Change-token source: explicit registration

Instead of relying on `Configure<T>(IConfiguration)` to implicitly register `IOptionsChangeTokenSource<T>`, the generated extension method registers `ConfigurationChangeTokenSource<T>` directly, so `IOptionsMonitor<T>` reload-on-change still works without any hidden reflection.

## CI verification

### Build-time analysis

The `tests/ConfigBoundNET.AotTests` project sets:

```xml
<IsAotCompatible>true</IsAotCompatible>
```

This enables the AOT, trim, and single-file analyzers as a bundle. Combined with the repo-wide `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, any IL2026/IL3050/IL2091 warning from generated code becomes an immediate build error.

### Runtime verification

The `.github/workflows/aot.yml` CI workflow does two things on every push and PR:

1. **Build** — runs the AOT analyzer at `Release` configuration
2. **Publish** — runs a full `dotnet publish` with Native AOT on Linux x64, executing the resulting native binary

The smoke test program exercises every supported `BindingStrategy` (string, numerics, bool, Guid, TimeSpan, DateTime, Uri, enums, nested configs, arrays) and asserts that every value round-trips correctly through the generated binder.

## Known AOT limitations

### `[MinLength]` and `[MaxLength]` attributes

The BCL's `MinLengthAttribute` and `MaxLengthAttribute` constructors are annotated with `[RequiresUnreferencedCode]` because their runtime implementations use reflection to find a `Count` property. ConfigBoundNET does **not** call their runtime validation — it only reads the attribute as a compile-time marker and emits its own explicit `.Length`/`.Count` check. However, the trim analyzer still flags the attribute constructor call at the use-site.

**Workaround**: Use `[StringLength(max, MinimumLength = min)]` instead, which is AOT-clean. Or suppress IL2026 for those specific lines if you're sure the runtime path isn't hit.

### Third-party configuration providers

ConfigBoundNET's generated code only calls `IConfigurationSection` methods (`section["Key"]`, `GetSection`, `GetChildren`, `Exists`), which are all interface calls and AOT-safe. However, the underlying configuration provider (JSON, environment variables, Azure Key Vault, etc.) may have its own AOT limitations. ConfigBoundNET cannot control or diagnose those.

## Verifying AOT compatibility locally

```bash
# Fast — static analysis only (no C++ toolchain needed):
dotnet build tests/ConfigBoundNET.AotTests/ConfigBoundNET.AotTests.csproj

# Full — Native AOT publish (requires C++ AOT toolchain; on Linux it ships with the SDK):
dotnet publish tests/ConfigBoundNET.AotTests/ConfigBoundNET.AotTests.csproj -c Release
```

A successful run prints:

```
[AOT smoke] OK — every supported BindingStrategy round-tripped through the generated binder.
```
