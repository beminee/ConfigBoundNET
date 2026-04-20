# JSON Schema Emission

ConfigBoundNET can emit a draft 2020-12 JSON Schema describing every `[ConfigSection]` type in your assembly. Referenced via `"$schema"` in `appsettings.json`, it gives you IDE IntelliSense, validation red-squiggles, and enum dropdowns inside the JSON editor — no runtime cost, no extra annotations to learn, no third-party tools.

```json
{
  "$schema": "./appsettings.schema.json",
  "Db": {
    "Conn": "Server=localhost;Database=App;Trusted_Connection=True;",
    "Prt":  5432          // ← IDE flags: "Property 'Prt' not allowed"
  }
}
```

## Quick start

1. Add the opt-in property to your `.csproj`:

   ```xml
   <PropertyGroup>
     <ConfigBoundEmitSchema>true</ConfigBoundEmitSchema>
   </PropertyGroup>
   ```

2. Build the project.

3. Open `appsettings.json` and add `"$schema": "./appsettings.schema.json"` at the top.

That's it. VS Code, Visual Studio 2022+, and JetBrains Rider all pick up the schema automatically once `$schema` is wired.

## What the schema covers

The schema mirrors the full `[ConfigSection]` graph at build time. Everything the generator already knows about your types flows through to the JSON:

| Feature | JSON Schema fragment |
|---|---|
| Non-nullable reference property | name added to parent's `"required": [...]` |
| `string` | `"type": "string"` |
| Integer types (`int`, `long`, `byte`, …) | `"type": "integer"` |
| Floating-point (`float`, `double`, `decimal`) | `"type": "number"` |
| `bool` | `"type": "boolean"` |
| `Guid` | `"type": "string", "format": "uuid"` |
| `DateTime`, `DateTimeOffset` | `"type": "string", "format": "date-time"` |
| `TimeSpan` | `"type": "string"` |
| `Uri` | `"type": "string", "format": "uri"` |
| Enum property | `"type": "string", "enum": ["Member1", "Member2", ...]` |
| Nullable value type (`int?`, `MyEnum?`) | `"type": ["integer", "null"]` etc. |
| Nested `[ConfigSection]` | recursively inlined object schema |
| Arrays / lists | `"type": "array", "items": { ... }` |
| Dictionaries with `string` keys | `"type": "object", "additionalProperties": { ... }` |
| `List<[ConfigSection]>`, `Dictionary<string, [ConfigSection]>` | `items` / `additionalProperties` recurse into the element/value type |
| `[Range(min, max)]` | `"minimum": min, "maximum": max` |
| `[StringLength(max, MinimumLength = n)]` | `"maxLength": max, "minLength": n` |
| `[MinLength(n)]` / `[MaxLength(n)]` on strings | `"minLength"` / `"maxLength"` |
| `[MinLength(n)]` / `[MaxLength(n)]` on collections | `"minItems"` / `"maxItems"` |
| `[RegularExpression(pattern)]` | `"pattern": "..."` |
| `[Url]` | `"format": "uri"` |
| `[EmailAddress]` | `"format": "email"` |
| `[AllowedValues(...)]` | `"enum": [...]` (overrides enum member list if both present) |
| `[DeniedValues(...)]` | `"not": { "enum": [...] }` |
| `[Sensitive]` | `"writeOnly": true, "description": "Sensitive — redacted in logs."` |
| Attribute `ErrorMessage` | `"description": "..."` (when no description is already set) |

Root object gets `"additionalProperties": true` so your `appsettings.json` can still carry framework-reserved sections (`Logging`, `AllowedHosts`, `ConnectionStrings`). Each `[ConfigSection]` object gets `"additionalProperties": false` so typos inside the sections you own become red squiggles.

## The two-step pipeline

Roslyn source generators can only emit `.cs` files via `context.AddSource`; they cannot drop arbitrary files on disk. ConfigBoundNET therefore splits schema delivery into two steps, and both ship in the same NuGet package.

```
         Source generator                         MSBuild task
   (compile-time, in Roslyn)               (post-Build, in MSBuild)
   ────────────────────────────            ──────────────────────────────
1. Collect every ConfigSection    →    3. Load the just-built assembly
   model                                   via MetadataLoadContext
2. Emit ConfigBoundJsonSchema.cs  →    4. Read ConfigBoundJsonSchema.Json
   with the schema as a const              const via GetRawConstantValue
                                       5. WriteAllText to $(ConfigBoundSchemaOutputPath)
                                          (skipped when content unchanged)
```

### Step 1: the `const string` — always emitted

Whenever your compilation has at least one `[ConfigSection]` type, the generator emits:

```csharp
namespace ConfigBoundNET;

public static class ConfigBoundJsonSchema
{
    public const string Json = @"{ ""$schema"": ..., ""properties"": { ... } }";
}
```

This happens **regardless** of `ConfigBoundEmitSchema` — the cost is a single verbatim-string constant in your assembly. You can access it at runtime:

```csharp
// Runtime access — always works, no MSBuild opt-in required.
string schema = ConfigBoundNET.ConfigBoundJsonSchema.Json;
await File.WriteAllTextAsync("/tmp/schema.json", schema);
```

Useful cases for the const at runtime:

- Expose a `/schema` endpoint in dev builds so anyone on the team can grab the current schema without a local checkout.
- Ship a CLI tool that takes a `.csproj` path and a target directory and writes the schema there.
- Round-trip the schema through `JsonDocument.Parse` as a smoke test that no `[ConfigSection]` type produced malformed output.

### Step 2: the MSBuild task — opt-in via `ConfigBoundEmitSchema`

The step that writes `appsettings.schema.json` to disk is gated behind `<ConfigBoundEmitSchema>true</ConfigBoundEmitSchema>`, defaulting to off. A shipped MSBuild target (`build/ConfigBoundNET.targets` inside the NuGet) runs after `Build` and invokes `ConfigBoundNET.Build.EmitConfigBoundSchemaTask`. The task:

1. Loads the just-built assembly via `MetadataLoadContext` — reflection-only, no user code runs, no trim concerns.
2. Looks up `ConfigBoundNET.ConfigBoundJsonSchema` and reads `Json` via `FieldInfo.GetRawConstantValue`, which returns the embedded metadata literal without executing IL.
3. Writes the value to `$(ConfigBoundSchemaOutputPath)` only when the content differs from what's already on disk (so `mtime` stays stable across no-op rebuilds — downstream MSBuild targets don't cascade-rebuild).

The task DLL is multi-targeted (`net472` for Visual Studio / .NET Framework MSBuild, `net10.0` for `dotnet build`). `ConfigBoundNET.targets` picks the right one via `$(MSBuildRuntimeType)`.

## Configuration

### `<ConfigBoundEmitSchema>` — opt in

```xml
<PropertyGroup>
  <ConfigBoundEmitSchema>true</ConfigBoundEmitSchema>
</PropertyGroup>
```

Default: `false`. Off by default so upgrading the package doesn't cause a new tracked file to appear in existing consumers' repos.

### `<ConfigBoundSchemaOutputPath>` — override the path

```xml
<PropertyGroup>
  <ConfigBoundEmitSchema>true</ConfigBoundEmitSchema>
  <ConfigBoundSchemaOutputPath>$(MSBuildProjectDirectory)\config\schema.json</ConfigBoundSchemaOutputPath>
</PropertyGroup>
```

Default: `$(MSBuildProjectDirectory)\appsettings.schema.json`.

The default lives next to the project file so `"$schema": "./appsettings.schema.json"` in a committed `appsettings.json` resolves correctly. Commit both files — the schema is deterministic output from your types and contributors benefit from IntelliSense before their first local build.

> **Tip**: If you parameterise the path with MSBuild-computed properties like `$(TargetDir)` or `$(OutDir)`, set it inside a `BeforeTargets="EmitConfigBoundSchema"` target. Those properties aren't resolved during static property evaluation; a target-scoped `<PropertyGroup>` re-evaluates them at run time. The AOT smoke test in `tests/ConfigBoundNET.AotTests/` uses this pattern.

### `<ConfigBoundSchemaTaskAssembly>` — override the task DLL path

Only relevant if you reference ConfigBoundNET via `ProjectReference` (i.e. you're developing on the repo itself). Real consumers who pull the NuGet leave this unset; the targets file resolves the task DLL out of `tasks/<tfm>/` inside the restored package.

## Example

Given this configuration:

```csharp
[ConfigSection("Db")]
public partial record DbConfig
{
    [Sensitive]
    public string Conn { get; init; } = default!;

    [Range(1, 65535)]
    public int Port { get; init; } = 5432;

    public LogLevel MinLevel { get; init; } = LogLevel.Info;

    public List<EndpointConfig> Replicas { get; init; } = new();
}

public enum LogLevel { Trace, Debug, Info, Warn, Error }

[ConfigSection("__endpoint__")]
public partial record EndpointConfig
{
    [RegularExpression(@"^https?://")]
    public string Url { get; init; } = default!;
}
```

the emitted `appsettings.schema.json` is:

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "title": "ConfigBoundNET generated schema",
  "type": "object",
  "additionalProperties": true,
  "properties": {
    "Db": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "Conn": {
          "type": "string",
          "writeOnly": true,
          "description": "Sensitive value — redacted in logs."
        },
        "Port": {
          "type": "integer",
          "minimum": 1,
          "maximum": 65535
        },
        "MinLevel": {
          "type": "string",
          "enum": ["Trace", "Debug", "Info", "Warn", "Error"]
        },
        "Replicas": {
          "type": "array",
          "items": {
            "type": "object",
            "additionalProperties": false,
            "properties": {
              "Url": { "type": "string", "pattern": "^https?://" }
            },
            "required": ["Url"]
          }
        }
      },
      "required": ["Conn"]
    },
    "__endpoint__": { ... }
  }
}
```

Note that `EndpointConfig` appears both inlined inside `Db.Replicas.items` and as its own top-level section `__endpoint__` — ConfigBoundNET doesn't know which section names are "real" vs "placeholder for element types", so it emits every `[ConfigSection]` as a root property. Editors tolerate extras thanks to `additionalProperties: true` at the root.

## Caveats

### Cross-assembly nested `[ConfigSection]` types

If your `OuterConfig` has a property typed as `InnerConfig`, and `InnerConfig` is defined in a different assembly that the generator doesn't analyze (typically a shared NuGet), the schema walker can't inline the sub-schema. It falls back to `{ "type": "object", "additionalProperties": true }` for that property and raises **[CB0011](diagnostics.md#cb0011-nested-configsection-type-defined-outside-this-compilation)** (info severity) so you know the IntelliSense is degraded there. Runtime binding and validation are unaffected.

### .NET regex ≠ ECMA 262

`[RegularExpression]` patterns are emitted verbatim into `"pattern"`. JSON Schema requires ECMA 262 regex; .NET regex is a slightly different dialect. Most real-world config validators (anchors, simple character classes, basic quantifiers) round-trip fine; exotic .NET-only constructs (atomic groups, named backreferences, balanced groups) may behave differently in the editor than at runtime. We don't filter or translate patterns — the pattern you wrote is the pattern the editor sees.

### MSBuild incrementality

We intentionally do **not** use MSBuild's declarative `Inputs` / `Outputs` on the `EmitConfigBoundSchema` target. Resolving a stable output path would require `$(OutDir)`-style properties that aren't fully populated during Outputs-evaluation. Instead, the task's own `WriteIfChanged` short-circuits on unchanged content, which preserves the file's `mtime` and gives downstream targets the same incremental-build contract. A no-op rebuild leaves `appsettings.schema.json`'s mtime stable.

### `$ref` is not used

Deeply recursive or diamond-shape configs (same nested type reached by multiple paths) are inlined at every call site. For typical config sizes (tens of KB at most) this produces perfectly usable schemas; if you have a genuine reason to keep the schema small, file an issue and we'll look at adding `$ref` emission.

## Related

- **[Diagnostics: CB0011](diagnostics.md#cb0011-nested-configsection-type-defined-outside-this-compilation)** — the info-level diagnostic raised when a nested `[ConfigSection]` lives in another assembly.
- **[Architecture](architecture.md)** — the incremental pipeline that produces the schema.
- **[AOT and Trimming](aot-and-trimming.md)** — why the generator and task both use reflection-only APIs (`MetadataLoadContext`, `GetRawConstantValue`) and how that interacts with Native AOT.
