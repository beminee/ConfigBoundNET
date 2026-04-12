# Diagnostics Reference

All ConfigBoundNET diagnostics fire at **build time** inside the IDE and during `dotnet build`. IDs are in the `CB0xxx` range and are stable public contract — you can reference them in `<NoWarn>`, `.editorconfig`, or `#pragma warning disable`.

## Error diagnostics (fail the build)

### CB0001: Config type must be declared partial

```csharp
// Error: DbConfig is not partial
[ConfigSection("Db")]
public record DbConfig { ... }  // CB0001
```

**Fix**: Add the `partial` modifier. The IDE code fix offers this as a one-click lightbulb action.

```csharp
[ConfigSection("Db")]
public partial record DbConfig { ... }  // OK
```

**Why**: The generator extends the type with a nested `Validator` class, a constructor, and a `SectionName` constant. C# requires the `partial` modifier to split a type across multiple declarations.

---

### CB0002: Configuration section name cannot be empty

```csharp
[ConfigSection("")]   // CB0002
[ConfigSection("  ")] // CB0002
```

**Fix**: Supply a non-empty section name, or use the parameterless form to infer from the type name. The IDE code fix offers "Use '{Inferred}' as section name".

```csharp
[ConfigSection("Db")]  // explicit
[ConfigSection]         // inferred from type name — OK
```

**Why**: An empty section name means the generator doesn't know which `IConfiguration` path to bind against. The parameterless constructor is the deliberate "infer it" signal; an explicit empty string is a mistake.

---

### CB0003: Nested config types are not supported

```csharp
public class Outer
{
    [ConfigSection("Db")]
    public partial record DbConfig { ... }  // CB0003 — nested inside Outer
}
```

**Fix**: Move the type to namespace scope. The IDE code fix offers "Move type to namespace scope".

**Why**: Extending a nested type with `partial` requires every containing type to also be partial. Rather than chaining that complexity, ConfigBoundNET rejects nested types and asks you to move them out.

---

### CB0005: Unsupported target for [ConfigSection]

```csharp
[ConfigSection("Db")]
public partial struct DbConfig { ... }  // CB0005 — struct
```

**Fix**: Change to `class` or `record`. The IDE code fix offers "Change to class".

**Why**: `IOptions<T>` hands out struct copies, not references. Mutations on the copy don't propagate back, making struct options a well-known anti-pattern. ConfigBoundNET rejects them to prevent this class of bug.

---

### CB0008: [RegularExpression] pattern is not a valid regex

```csharp
[RegularExpression("[invalid")]  // CB0008 — unterminated character class
public string Name { get; init; } = default!;
```

**Fix**: Fix the regex pattern. The error message includes the `System.Text.RegularExpressions.Regex` exception message so you can see exactly what's wrong.

**Why**: The generator validates the pattern at compile time by attempting `new Regex(pattern)` inside the generator process. This catches typos instantly instead of at runtime.

## Warning diagnostics (build succeeds, but something is likely wrong)

### CB0004: Config type has no bindable properties

```csharp
[ConfigSection("Db")]
public partial record DbConfig;  // CB0004 — no public writable properties
```

**Why**: A type with no properties has nothing to bind from configuration. The generator still emits the extension method (it's harmless), but the warning suggests you forgot to add properties.

---

### CB0006: [Range] applied to non-numeric property

```csharp
[Range(1, 100)]
public string Name { get; init; } = default!;  // CB0006 — string is not numeric
```

**Why**: `[Range]` generates a `< min || > max` comparison, which is meaningless on strings. The annotation is ignored.

---

### CB0007: Length attribute applied to non-string/non-collection property

```csharp
[StringLength(100)]
public int Port { get; init; }  // CB0007 — int has no .Length
```

**Why**: `[StringLength]`, `[MinLength]`, and `[MaxLength]` check `.Length` (strings/arrays) or `.Count` (lists/dictionaries). They're meaningless on scalar types. The annotation is ignored.

Note: Collections are accepted — `[MinLength(1)]` on a `List<string>` is valid and checks `.Count`.

---

### CB0010: Property type is not bindable by ConfigBoundNET

```csharp
public object Payload { get; init; } = default!;  // CB0010 — object is unsupported
public HashSet<string> Tags { get; init; } = new();  // CB0010 — HashSet unsupported
```

**Why**: The reflection-free binder only supports types it knows how to parse. See [Configuration Binding](configuration-binding.md) for the full supported type list. The property is skipped at binding time and retains its C#-declared default.

## Informational diagnostics

### CB0009: [Required] is redundant on non-nullable property

```csharp
[Required]
public string Name { get; init; } = default!;  // CB0009 — already non-nullable
```

**Why**: Non-nullable reference types are automatically validated. The `[Required]` attribute adds no value. The IDE code fix offers "Remove redundant [Required]".

## Suppressing diagnostics

Per-property:
```csharp
#pragma warning disable CB0010
public object Payload { get; init; } = default!;
#pragma warning restore CB0010
```

Per-project (in `.csproj`):
```xml
<NoWarn>$(NoWarn);CB0010</NoWarn>
```

Per-file (in `.editorconfig`):
```ini
[*.cs]
dotnet_diagnostic.CB0010.severity = none
```

## Code fix providers

| Diagnostic | Fix | Action |
|---|---|---|
| CB0001 | Add `partial` modifier | Inserts `partial` keyword |
| CB0002 | Use '{Inferred}' as section name | Replaces empty string with name derived from type |
| CB0003 | Move type to namespace scope | Extracts the nested type to namespace level |
| CB0005 | Change to class | Replaces `struct` with `class` |
| CB0009 | Remove redundant [Required] | Deletes the `[Required]` attribute |

All code fixes support **Fix All** via `WellKnownFixAllProviders.BatchFixer`, so you can apply them across the entire solution in one action.
