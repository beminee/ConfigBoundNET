# Architecture and Design Decisions

This document explains the internal architecture of ConfigBoundNET and the reasoning behind key design choices. It's aimed at contributors and anyone curious about how Roslyn source generators work in practice.

## High-level pipeline

```
User source code
    ↓
[1] Roslyn finds [ConfigSection] via ForAttributeWithMetadataName
    ↓
[2] ModelBuilder.Build() transforms the Roslyn symbol into a flat, equatable model
    ↓
[3] SourceEmitter.Emit() renders the model to C# source text
    ↓
[4] Roslyn compiles the generated source into the user's assembly
```

This is the standard incremental source generator pattern. The critical invariant: **steps 2 and 3 are pure functions of equatable inputs**, so Roslyn can cache their outputs across edits. If the user changes an unrelated file, the generator doesn't re-run.

## Project structure

```
src/ConfigBoundNET/              → The generator (netstandard2.0, ships as analyzer)
src/ConfigBoundNET.CodeFixes/    → IDE code-fix providers (separate assembly per RS1038)
tests/ConfigBoundNET.Tests/      → xUnit tests
tests/ConfigBoundNET.AotTests/   → AOT smoke-test console app
examples/ConfigBoundNET.Example/ → Minimal Generic Host example
examples/ConfigBoundNET.WebApi/  → Web API example with complex config
```

### Why two src projects?

Roslyn enforces **RS1038**: source generators must not reference `Microsoft.CodeAnalysis.Workspaces`, because that assembly isn't available during command-line compilation. Code fix providers need Workspaces for document editing (`CodeAction`, `Document.WithSyntaxRoot`, etc.). The split is mandatory, not a style choice.

Both DLLs ship in the NuGet's `analyzers/dotnet/cs` folder so IDEs load them together.

## Key design decisions

### 1. Post-init attribute emission

The `[ConfigSection]` attribute is emitted as C# source via `RegisterPostInitializationOutput`, not shipped as a compiled DLL. This means:

- Consumers need a single package reference, not two (no separate "ConfigBoundNET.Abstractions" runtime package)
- The attribute is `internal` in each consuming assembly, so two projects using ConfigBoundNET don't collide
- The attribute is decorated with `[Conditional("CONFIGBOUNDNET_KEEP_ATTRIBUTES")]` so it's stripped from IL unless the consumer explicitly opts in

The `ConfigBoundOptionsFactory<T>` helper is also emitted this way, for the same reasons.

### 2. Constructor-based binding (not static method)

The generated binder is an **instance constructor**, not a static `Bind(IConfigurationSection, T)` method:

```csharp
public DbConfig(IConfigurationSection section)
{
    this.Conn = section["Conn"];  // legal: init-only write from constructor
}
```

This is the only AOT-safe way to populate `init`-only properties. C# permits writes to `init` properties from:
- Object initializers
- Instance constructors
- Other `init` accessors

A static method on the same type does **not** qualify (CS8852). We discovered this during implementation when the original `static void Bind(section, options)` design failed to compile for records with `init`-only properties.

### 3. OptionsFactory replacement (not IConfigureOptions)

The standard way to bind config is `services.Configure<T>(IConfiguration)`, which registers an `IConfigureOptions<T>` that calls `ConfigurationBinder.Bind`. We can't use this because:

1. `ConfigurationBinder.Bind` uses reflection (defeats AOT)
2. `IConfigureOptions<T>.Configure(T options)` receives an already-constructed instance, and `init`-only properties can't be written from an `Action<T>` callback

Instead, we replace the `IOptionsFactory<T>` entirely with `ConfigBoundOptionsFactory<T>`, which overrides `CreateInstance` to call our generated constructor. The base `OptionsFactory<T>` still runs all `IConfigureOptions`, `IPostConfigureOptions`, and `IValidateOptions` passes after construction.

### 4. Flat equatable models (no Roslyn symbols in the pipeline)

Every model type (`ConfigSectionModel`, `ConfigPropertyModel`, `DataAnnotationModel`, etc.) is a sealed record containing only:
- `string` / `string?`
- `bool`
- `int` / `double`
- `enum` values
- `EquatableArray<T>` (a thin wrapper over `T[]` with structural equality)

**Never** `ISymbol`, `ITypeSymbol`, `SyntaxNode`, or any other Roslyn type. This is critical because:

- Roslyn types use reference equality, which defeats incremental caching
- Roslyn types hold references to the compilation, which pins large object graphs in memory
- The separation forces a clean boundary: `ModelBuilder` is the only code that touches Roslyn APIs; `SourceEmitter` operates on plain data

The custom `EquatableArray<T>` exists because `ImmutableArray<T>` also uses reference equality, and arrays have no structural equality. It's the standard pattern recommended by the .NET team for incremental generators.

### 5. Explicit per-attribute validation (not Validator.TryValidateObject)

DataAnnotations validation could have been implemented with a single line:

```csharp
Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
```

We chose explicit `if` statements instead because:

- `TryValidateObject` uses reflection to enumerate attributes at runtime — defeats AOT
- Build-time validation of misapplied attributes (CB0006–CB0009) is impossible with the runtime path
- The generator can validate regex patterns at compile time (CB0008)
- Each check is visible in the generated source, making debugging transparent

The trade-off is more generated code and one emit method per attribute kind.

### 6. Regex fields as static readonly (not per-call)

`[RegularExpression]` patterns are compiled once as `private static readonly Regex` fields on the `Validator` class:

```csharp
private static readonly Regex _cb_Regex_Endpoint =
    new(@"^https?://", RegexOptions.Compiled);
```

Not `new Regex(pattern)` inside `Validate()`, which would recompile the regex on every validation call. The `RegexOptions.Compiled` flag tells the BCL to JIT-compile the pattern into IL for maximum throughput.

### 7. Section name inference via suffix stripping

When `[ConfigSection]` is used without an argument, the section name is derived from the type name by stripping common suffixes in order: `Configuration` > `Settings` > `Options` > `Config`. This logic lives in `SectionNameHelper.InferSectionName` and is shared between the generator (for inference) and the code fix provider (for the "Use '{Inferred}' as section name" lightbulb action).

The order matters: `Configuration` is tried before `Config` because `DbConfiguration` should become `"Db"`, not `"DbConfigur"`.

### 8. Change-token propagation without Configure&lt;T&gt;

Since we bypass `services.Configure<T>(IConfiguration)`, we lose its implicit `IOptionsChangeTokenSource<T>` registration. The generated extension method registers it explicitly:

```csharp
services.TryAddEnumerable(
    ServiceDescriptor.Singleton<IOptionsChangeTokenSource<DbConfig>>(
        new ConfigurationChangeTokenSource<DbConfig>(Options.DefaultName, section)));
```

This ensures `IOptionsMonitor<T>` still reacts to configuration reloads (file edits, environment variable changes, etc.) even though we're not using the standard binding path.

## Testing strategy

| Layer | What's tested | How |
|---|---|---|
| Generator output | Substring assertions on emitted C# | `GeneratorHarness.Run()` + `Assert.Contains` |
| Diagnostics | Correct diagnostic IDs fire for bad input | `GeneratorHarness.Run()` + `Assert.Contains(result.Diagnostics, ...)` |
| End-to-end binding | Bound values match config input | `GeneratorHarness.CompileAndBind()` — compiles augmented code, loads assembly, resolves `IOptions<T>.Value` |
| Validation failures | Bad values produce expected error messages | `Assert.ThrowsAny<Exception>(() => CompileAndBind(...))` |
| Emitter output stability | Exact generated source pinned | Verify.SourceGenerators snapshots (`.verified.cs` files) |
| AOT compatibility | Zero IL2026/IL3050/IL2091 warnings | `<IsAotCompatible>true</IsAotCompatible>` + `TreatWarningsAsErrors` |
| Code fix wiring | Correct diagnostic IDs on each provider | `Assert.Contains("CBxxxx", fix.FixableDiagnosticIds)` |
| Section name inference | Suffix stripping logic | `[Theory]` with `[InlineData]` on `SectionNameHelper.InferSectionName` |

The `CompileAndBind` harness is the most complex piece: it runs the generator, compiles the augmented output to a `MemoryStream`, loads the assembly via `Assembly.Load(byte[])`, builds an in-memory `IConfiguration`, drives the generated `Add{TypeName}` extension through a real `ServiceCollection.BuildServiceProvider()`, and resolves the bound instance. The reflection lives entirely in the test harness; the generated code is reflection-free.

## File-by-file guide

| File | Purpose |
|---|---|
| `AttributeSource.cs` | Embedded C# source for `ConfigSectionAttribute` and `ConfigBoundOptionsFactory<T>` |
| `ConfigBoundGenerator.cs` | The `IIncrementalGenerator` entry point — wires post-init + pipeline |
| `ConfigSectionModel.cs` | All model types + `ModelBuilder` (Roslyn → equatable model) + `DescriptorLookup` |
| `DataAnnotationModel.cs` | `DataAnnotationKind` enum + `DataAnnotationModel` record |
| `DiagnosticDescriptors.cs` | All 10 diagnostic descriptors (CB0001–CB0010) |
| `EquatableArray.cs` | Value-equatable `T[]` wrapper for incremental caching |
| `SectionNameHelper.cs` | Shared suffix-stripping logic for section name inference |
| `SourceEmitter.cs` | Renders models to C# source (constructor, validator, DI extension) |
| `CodeFixes/*.cs` | Five `CodeFixProvider` implementations (separate assembly) |
