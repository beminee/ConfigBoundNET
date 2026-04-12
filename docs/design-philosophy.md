# Design Philosophy

ConfigBoundNET exists because of a simple observation: **configuration bugs are the #1 cause of 3 AM pages that could have been caught at build time.**

## The five principles

### 1. Shift left: catch errors at build time, not runtime

The gold standard for configuration validation is: **if it's wrong, the build should fail.** The second-best is: **if it's wrong, the host should fail at startup.** The worst — and most common today — is: **the app starts fine and fails hours later when someone hits the code path that reads the misconfigured value.**

ConfigBoundNET targets the first two:
- Structural errors (missing `partial`, empty section name, wrong target type) fail the **build** via Roslyn diagnostics
- Value errors (null required field, out-of-range number, bad regex) fail at **startup** via `ValidateOnStart()`
- Nothing silently succeeds and breaks later

### 2. Zero runtime reflection

The .NET ecosystem is moving toward AOT and trimming. Libraries that use reflection for configuration binding (`ConfigurationBinder.Bind`, `Activator.CreateInstance`, `PropertyInfo.SetValue`) produce IL2026/IL3050 warnings and fail under Native AOT.

ConfigBoundNET generates explicit, statically-compiled C# code for every binding and validation operation. The generated constructor calls `section["Key"]` and `int.TryParse` directly. The validator uses `if` statements. The DI extension registers concrete types. There is nothing for the trimmer to cut and nothing for the AOT compiler to warn about.

### 3. Single package, zero ceremony

Adding ConfigBoundNET to a project is one line:

```xml
<PackageReference Include="ConfigBoundNET" Version="1.0.0" />
```

There is no separate "ConfigBoundNET.Abstractions" package. The `[ConfigSection]` attribute and the `ConfigBoundOptionsFactory<T>` helper are emitted as `internal` types in the consumer's assembly via post-initialization output. This means:
- No runtime dependency to version-match
- No diamond dependency if two libraries both use ConfigBoundNET
- The attribute is stripped from IL metadata by default (via `[Conditional]`)

### 4. Progressive complexity

A minimal usage requires exactly one attribute and one line of DI registration:

```csharp
[ConfigSection("Db")]
public partial record DbConfig
{
    public string Conn { get; init; } = default!;
}

// In Program.cs:
builder.Services.AddDbConfig(builder.Configuration);
```

From there, you can progressively add:
- DataAnnotations (`[Range]`, `[RegularExpression]`, etc.) — still zero boilerplate
- Custom cross-field validation (`partial void ValidateCustom`) — one method, no interfaces to implement
- Nested config types — just annotate the inner type too
- Collections — just declare the property

Each layer adds capability without forcing you to restructure what you already have. A `partial record` with one string property and a `partial record` with 15 properties, 3 nested types, 8 DataAnnotations, and a custom hook use the same pattern.

### 5. Transparent generated code

The generated code is designed to be **readable**. If you set `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>`, you can open the generated `.g.cs` files and see exactly what ConfigBoundNET produces:

- The `SectionName` constant
- The reflection-free constructor with one `TryParse` per property
- The `Validator` class with one `if` per check
- The DI extension with the factory replacement
- The `ValidateCustom` hook declaration

There are no hidden intermediaries, no dynamic dispatch, no magic strings. If the generated code breaks, you can read it. If the generated code is correct but your config is wrong, the error message tells you the exact section and property.

## What ConfigBoundNET is not

### Not a configuration source

ConfigBoundNET doesn't replace `appsettings.json`, Azure App Configuration, environment variables, or any other `IConfigurationProvider`. It sits on top of whatever `IConfiguration` you've already wired up and provides the strongly-typed, validated, AOT-safe bridge from flat key-value pairs to C# objects.

### Not a replacement for IOptions<T>

ConfigBoundNET generates code that plugs into the standard `IOptions<T>` / `IOptionsMonitor<T>` / `IOptionsSnapshot<T>` framework. It replaces the **binding** and **validation** layers, not the caching, change-notification, or DI lifetime management. You still inject `IOptions<DbConfig>` and call `.Value`.

### Not a schema generator

ConfigBoundNET doesn't produce JSON schemas, environment variable documentation, or configuration UI. The type itself is the schema. If you need machine-readable schema output, that would be a separate tool that reads the same `[ConfigSection]` types and produces the format you need.

## Trade-offs we accepted

| Trade-off | Why it's acceptable |
|---|---|
| More generated code per type | Generated code is machine-managed; it doesn't bloat your source tree unless you opt in to `EmitCompilerGeneratedFiles` |
| Fixed set of supported types | The types we support cover 95%+ of real-world config properties. Unsupported types get a build-time warning (CB0010) so you know immediately |
| No `List<ComplexType>` yet | Array-of-objects binding requires recursive section enumeration; it's tractable but hasn't been demanded enough to prioritize |
| Regex in generator process | Validating `[RegularExpression]` patterns at compile time means the generator calls `new Regex(pattern)` inside the compiler. This is safe (patterns are constant strings, compilation is fast) but unusual for a generator |
| Constructor, not Configure<T> | The `init`-only property constraint forced us into constructor-based binding, which required replacing `IOptionsFactory<T>`. This is more invasive than `IConfigureOptions<T>` but is the only AOT-safe approach for records |
