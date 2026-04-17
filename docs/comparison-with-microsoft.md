# Comparison with Microsoft's Built-in Generators

.NET ships two source generators that overlap with ConfigBoundNET's functionality. This page explains what each one does, where they overlap, where they don't, and when you might choose one over the other.

## The two Microsoft generators

### 1. `[OptionsValidator]` — validation only

Available since .NET 8 via `Microsoft.Extensions.Options`. You write an empty partial class:

```csharp
[OptionsValidator]
public partial class ValidateSettingsOptions : IValidateOptions<SettingsOptions> { }
```

The generator fills in the `Validate` method with per-property DataAnnotations checks. It replaces some reflection-heavy attributes (`[Range]`, `[MinLength]`, `[MaxLength]`) with AOT-friendly source-generated versions.

**What it does NOT do:**
- Does not bind configuration (you still call `.Bind(config.GetSection("Name"))`)
- Does not generate DI extension methods
- Does not manage section names
- Does not infer required-ness from nullability
- Does not support custom cross-field validation
- Does not produce build-time diagnostics for misuse
- Does not offer IDE code fixes

### 2. `EnableConfigurationBindingGenerator` — binding only

Available since .NET 8 via an MSBuild property:

```xml
<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>
```

This intercepts `services.Configure<T>(IConfiguration)` and `config.GetSection("X").Get<T>()` calls and generates AOT-safe binding code, replacing the reflection-based `ConfigurationBinder`.

**What it does NOT do:**
- Does not validate anything
- Does not generate DI extension methods
- Does not manage section names
- Does not generate validators

## Feature comparison

| Feature | Microsoft `[OptionsValidator]` | Microsoft `EnableConfigurationBindingGenerator` | **ConfigBoundNET** |
|---|---|---|---|
| **AOT-safe validation** | Yes | N/A | Yes |
| **AOT-safe binding** | No | Yes | Yes |
| **Single attribute, everything generated** | No (need both + manual wiring) | No (MSBuild property + manual code) | **Yes** (`[ConfigSection]` only) |
| **DI extension method** | No | No | **Yes** (`services.AddXConfig(config)`) |
| **Section name as constant** | Manual (`const string`) | No | **Yes** (`TypeName.SectionName`) |
| **Section name inference** | No | No | **Yes** (strips Config/Options/Settings suffix) |
| **Required from nullability** | No (need explicit `[Required]`) | N/A | **Yes** (non-nullable = required) |
| **Custom cross-field validation** | No | N/A | **Yes** (`partial void ValidateCustom`) |
| **Nested config binding** | N/A | Partial | **Yes** (recursive constructor) |
| **Nested config validation** | `[ValidateObjectMembers]` | N/A | **Yes** (automatic recursion) |
| **Collection binding** | N/A | Yes | **Yes** (`T[]`, `List<T>`, `Dictionary<string, T>`) |
| **Build-time diagnostics** | No | No | **Yes** (CB0001–CB0010) |
| **IDE code fixes** | No | No | **Yes** (5 lightbulb actions) |
| **Build-time regex validation** | No | N/A | **Yes** (CB0008) |
| **`ErrorMessage` on attributes** | Yes | N/A | **Yes** (`{0}`, `{1}`, `{2}` substituted at generator time) |
| **Change-token / reload** | N/A | Implicit via `Configure<T>` | **Yes** (explicit `ConfigurationChangeTokenSource`) |
| **Ships with SDK** | Yes | Yes | No (NuGet package) |
| **Package count** | 0 (built-in) | 0 (built-in) | 1 |

## What it takes to get the full picture with Microsoft's generators

To achieve what ConfigBoundNET gives you with one attribute + one DI line, you need:

```csharp
// 1. The options class (no section name constant unless you add one manually)
public sealed class SettingsOptions
{
    public const string ConfigurationSectionName = "MySection";  // manual

    [Required]                    // must be explicit — nullability not inferred
    public required string SiteTitle { get; set; }

    [Required]
    [Range(0, 1000)]
    public required int? Scale { get; set; }
}

// 2. An empty validator class (one per options type)
[OptionsValidator]
public partial class ValidateSettingsOptions : IValidateOptions<SettingsOptions> { }

// 3. Manual DI wiring (section name is a magic string unless you use the const)
builder.Services
    .AddOptions<SettingsOptions>()
    .Bind(builder.Configuration.GetSection(SettingsOptions.ConfigurationSectionName));

builder.Services
    .AddSingleton<IValidateOptions<SettingsOptions>, ValidateSettingsOptions>();
```

Plus you need `<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>` in the csproj for AOT-safe binding.

The ConfigBoundNET equivalent:

```csharp
[ConfigSection("MySection")]
public partial record SettingsOptions
{
    public string SiteTitle { get; init; } = default!;   // required (inferred)

    [Range(0, 1000)]
    public int Scale { get; init; }
}

// One line:
builder.Services.AddSettingsOptions(builder.Configuration);
```

## Where Microsoft's approach is better

1. **First-party** — ships with the SDK, no extra NuGet dependency to adopt or version-manage.

2. **`[ValidateObjectMembers]`** — explicit opt-in for nested validation gives fine-grained control over which nested types get validated. ConfigBoundNET validates all `[ConfigSection]`-annotated nested types automatically.

3. **Attribute replacement** — Microsoft's generator replaces `[Range]`, `[MinLength]`, `[MaxLength]` with source-generated AOT-friendly attribute implementations that faithfully reproduce the original BCL behavior (including `IComparable`-based comparison, `OperandType` conversion, exclusive bounds). ConfigBoundNET emits simpler `if (x < min || x > max)` checks, which are sufficient for configuration but don't support the full `ValidationAttribute` contract.

4. **Broader adoption** — as a built-in feature, it's more likely to be familiar to the .NET community and has Microsoft's long-term support commitment.

## Where ConfigBoundNET is better

1. **Single-attribute experience** — one `[ConfigSection]` replaces both generators plus manual wiring. Less ceremony, fewer things to forget.

2. **Section name management** — the section name is part of the type's contract, not a magic string scattered across `Program.cs`. Inference from the type name means you often don't even write it.

3. **Nullability-based required inference** — `string Conn` is required; `string? Conn` is optional. No need for `[Required]` on every non-nullable property.

4. **Custom cross-field validation** — `partial void ValidateCustom(List<string> failures)` is a zero-cost escape hatch. Microsoft's generator has no equivalent; you'd need to hand-write a separate `IValidateOptions<T>` for cross-field rules.

5. **Build-time diagnostics** — 10 diagnostics catch structural errors (missing `partial`, empty section name, misapplied annotations, invalid regex) at build time. The Microsoft generators produce no diagnostics for misuse.

6. **IDE code fixes** — 5 lightbulb actions for common mistakes. The Microsoft generators have none.

7. **Integrated binding + validation** — one generator handles both, so they can't get out of sync. With Microsoft's approach, you can accidentally validate properties that the binding generator doesn't support, or bind properties the validator doesn't check.

## Can they coexist?

Yes. ConfigBoundNET and `[OptionsValidator]` operate on different attributes and don't interfere. You could use ConfigBoundNET for some config types and Microsoft's generator for others within the same project. The `EnableConfigurationBindingGenerator` MSBuild property is also orthogonal — it intercepts `Configure<T>()` calls, which ConfigBoundNET doesn't use.

However, there's no reason to use both on the same type. If you apply `[ConfigSection]`, ConfigBoundNET handles everything. If you prefer Microsoft's approach for a particular type, skip `[ConfigSection]` and wire it manually.

## Recommendation

- **Greenfield project, want minimum ceremony** → ConfigBoundNET
- **Existing project with many `Configure<T>()` calls, incremental migration** → Enable `EnableConfigurationBindingGenerator` first (zero code changes), then migrate to ConfigBoundNET type-by-type as needed
- **Enterprise project that requires first-party-only dependencies** → Microsoft's generators
- **Need custom cross-field validation without hand-writing `IValidateOptions<T>`** → ConfigBoundNET (Microsoft has no equivalent)
