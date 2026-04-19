# Benchmarks

Performance comparisons between **ConfigBoundNET** and Microsoft's source-generated alternatives:

- `EnableConfigurationBindingGenerator` — source-generated `section.Get<T>()` binding.
- `[OptionsValidator]` — source-generated `IValidateOptions<T>` validation.

Both Microsoft paths are the closest head-to-head targets: they're AOT-safe and reflection-free, same as ConfigBoundNET. Comparing against the reflection-based `ConfigurationBinder.Bind` baseline is intentionally out of scope. It loses by construction, and measuring that would only add noise to the comparison.

## Running

```bash
# Run every benchmark (takes a few minutes):
dotnet run -c Release --project benchmarks/ConfigBoundNET.Benchmarks

# Only the binding benchmarks:
dotnet run -c Release --project benchmarks/ConfigBoundNET.Benchmarks -- --filter '*Bind*'

# Only the validation benchmarks:
dotnet run -c Release --project benchmarks/ConfigBoundNET.Benchmarks -- --filter '*Validate*'

# Quick smoke run (single iteration, no statistical validity — useful for
# confirming the project runs after a change):
dotnet run -c Release --project benchmarks/ConfigBoundNET.Benchmarks -- --job dry

# List available benchmark methods without running:
dotnet run -c Release --project benchmarks/ConfigBoundNET.Benchmarks -- --list flat
```

Results are written to `benchmarks/ConfigBoundNET.Benchmarks/BenchmarkDotNet.Artifacts/`.

## What's measured

### `BindingBenchmarks`

Materialising a strongly-typed options instance from a live `IConfigurationSection`. The hot path every app hits at startup.

| Benchmark | Path |
|---|---|
| `ConfigBoundNET_Bind` | `new ConfigBoundAppConfig(section)` — generated `(IConfigurationSection)` ctor. |
| `MicrosoftSourceGen_Bind` | `section.Get<MicrosoftAppConfig>()` — intercepted by `EnableConfigurationBindingGenerator`. |

### `ValidationBenchmarks`

Running a validator against a pre-bound instance. The cost every startup pays with `ValidateOnStart()`.

| Benchmark | Path |
|---|---|
| `ConfigBoundNET_Validate` | `new ConfigBoundAppConfig.Validator().Validate(name, options)` — generated explicit-`if` chain. |
| `MicrosoftSourceGen_Validate` | `new MicrosoftAppConfigValidator().Validate(name, options)` — `[OptionsValidator]`-generated validator. |

### `GeneratorBenchmarks`

Measures the incremental source generator itself, how fast ConfigBoundNET runs during compilation. Answers the "what does this cost my build?" question directly, parameterised over project size.

| Benchmark | Path |
|---|---|
| `ColdRun` | Fresh `CSharpGeneratorDriver` + full compilation. First-build cost: no cached incremental outputs. |
| `Incremental_UnrelatedEdit` | Warmed driver + unrelated syntax tree added. Steady-state IDE cost: the user edits a file that has nothing to do with `[ConfigSection]`; every generator output should hit the Roslyn cache. |

Parameterised over `TypeCount = { 1, 10, 50 }`. A small script, a realistic medium project, and a large one. Healthy scaling: cold-run time grows sublinearly with type count; incremental time stays a fraction of cold-run time regardless of size.

Both benchmarks use two parallel config types (`ConfigBoundAppConfig`, `MicrosoftAppConfig`) with identical shape — scalars, a nested complex type, a `List<string>`, and a `Dictionary<string, string>`, plus a representative cross-section of DataAnnotations (`[Range]`, `[Url]`, `[EmailAddress]`, `[StringLength]`, `[Required]`). This isolates machinery cost from workload cost.

## Sample results

One representative run on a Windows 10 laptop, 13th Gen Intel Core i7-13700H, .NET 10.0.6, `DefaultJob`. Absolute numbers vary by hardware; what reproduces across machines is the *relative* ordering and allocation gap.

**Binding** (`section.Get<T>()` / `new T(section)`)

| Method | Mean | Ratio | Allocated | Alloc ratio |
|---|---:|---:|---:|---:|
| **ConfigBoundNET**: generated `(IConfigurationSection)` ctor | **2.51 μs** | **1.00×** | **3.76 KB** | **1.00×** |
| Microsoft: `EnableConfigurationBindingGenerator` | 4.59 μs | 1.83× | 7.65 KB | 2.04× |

**Validation** (`IValidateOptions<T>.Validate`)

| Method | Mean | Ratio | Allocated | Alloc ratio |
|---|---:|---:|---:|---:|
| **ConfigBoundNET**: generated `Validator.Validate` | **87.27 ns** | **1.00×** | **160 B** | **1.00×** |
| Microsoft: `[OptionsValidator]` generated validate | 184.76 ns | 2.12× | 968 B | 6.05× |

ConfigBoundNET comes out roughly **2× faster** on both axes with **2×–6× fewer allocations** on this hardware. The gap narrows on simpler types (fewer DataAnnotations, no nested type) and widens on more complex ones. The `[OptionsValidator]` generator leans heavily on `ValidateOptionsResultBuilder` and intermediate string allocations per check, while ConfigBoundNET emits plain `if (...) failures.Add(...)` lines. Re-run on your own hardware to verify.

**Generator runtime cost** (measures the source generator itself during compilation, across project sizes)

| TypeCount | Cold run | Incremental (unrelated edit) | Incremental ratio | Cold allocated | Incremental allocated |
|---:|---:|---:|---:|---:|---:|
|  1 |    604 μs |    314 μs | 0.52× |   254 KB |   128 KB |
| 10 |  1,053 μs |    643 μs | 0.61× |   804 KB |   339 KB |
| 50 |  2,875 μs |  1,893 μs | 0.66× | 3,231 KB | 1,274 KB |

Takeaways:
- **Cold scaling is strongly sublinear**: going from 1 to 50 types costs ~4.7× more, not 50×. Most of the cold-run cost is fixed Roslyn setup (BCL reference resolution, syntax-tree indexing); the per-type generator work is ~46 μs amortised.
- **Incremental path is always cheaper than cold**, roughly 0.5×–0.66× across sizes, with half the allocations or less. The remaining cost is Roslyn's per-compilation scan for `[ConfigSection]` attributes (that's unavoidable. The generator can't know a tree has no relevant types without looking at it); the actual per-type transforms hit the cache and skip emission.
- **Single-edit IDE cost** on a 50-type project is ~1.9 ms. Typing inside a file that doesn't touch any `[ConfigSection]` type adds this much to each incremental build.
- If the incremental ratio ever drifts above ~0.8× at any type count, it indicates a caching regression, probably a non-equatable type leaked into the pipeline model. [`GeneratorCacheTests`](../tests/ConfigBoundNET.Tests/GeneratorCacheTests.cs) pins the correctness side; this benchmark exposes the time-cost side.

## Confirming both Microsoft generators activated

To verify the comparison is real (both MS generators actually ran), rebuild with `-p:EmitCompilerGeneratedFiles=true` and inspect:

```
benchmarks/ConfigBoundNET.Benchmarks/obj/Release/net10.0/generated/
├── ConfigBoundNET/                                               ← our generator
├── Microsoft.Extensions.Configuration.Binder.SourceGeneration/   ← EnableConfigurationBindingGenerator
└── Microsoft.Extensions.Options.SourceGeneration/                ← [OptionsValidator]
```

All three directories should be present. If a Microsoft one is missing, the corresponding benchmark is quietly falling back to reflection and the comparison is invalid.

## What the numbers mean (and don't)

**Do:** compare relative orderings and order-of-magnitude gaps. `ConfigBoundNET_Bind` vs `MicrosoftSourceGen_Bind` on the same machine, same config, same run tells you which approach is faster for a realistic 10-field options type.

**Don't:** commit absolute numbers as hard targets. Results vary significantly by CPU, JIT version, OS scheduling, and kernel settings. Committing a baseline `.csv` and gating CI against it is an exercise in fighting noise.

This is a **local-run tool**. No CI step executes these benchmarks. If you land a PR and want to know "did this regress perf?", re-run locally on the same machine before and after — in-process `Ratio` columns are the only signal that survives hardware variance.
