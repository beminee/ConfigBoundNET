# Configuration Binding

ConfigBoundNET replaces `ConfigurationBinder.Bind` with a generated, reflection-free constructor. This page explains what types are supported, how collections work, and how nested config types compose.

## How binding works

For every `[ConfigSection]`-annotated type, the generator emits a constructor:

```csharp
public DatabaseConfig(IConfigurationSection section)
```

This constructor reads each property from the `IConfigurationSection` using explicit `section["PropertyName"]` indexing and type-specific `TryParse` calls. No reflection, no `Activator.CreateInstance`, no `ConfigurationBinder`.

Properties absent from the configuration section retain their C#-declared default values. For example:

```csharp
public int Port { get; init; } = 5432;  // stays 5432 if "Port" key is absent
```

## Supported scalar types

| C# type | Parsing method |
|---|---|
| `string` | Direct copy from `section["X"]` |
| `bool` | `bool.TryParse` |
| `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong` | `T.TryParse` with `NumberStyles.Integer` and `CultureInfo.InvariantCulture` |
| `float`, `double`, `decimal` | `T.TryParse` with `NumberStyles.Float` and `CultureInfo.InvariantCulture` |
| `Guid` | `Guid.TryParse` with `CultureInfo.InvariantCulture` |
| `TimeSpan` | `TimeSpan.TryParse` with `CultureInfo.InvariantCulture` |
| `DateTime` | `DateTime.TryParse` with `DateTimeStyles.RoundtripKind` |
| `DateTimeOffset` | `DateTimeOffset.TryParse` with `DateTimeStyles.RoundtripKind` |
| `Uri` | `Uri.TryCreate` with `UriKind.Absolute` |
| Any `enum` | `Enum.TryParse<T>` with `ignoreCase: true` |
| `Nullable<T>` | Wraps any value type above; null when key is absent |

All parsing uses **invariant culture** so configuration files are portable across machine locales. A value like `"0.75"` always uses a dot as the decimal separator regardless of the OS culture.

If a `TryParse` call fails (e.g. `"abc"` for an `int` property), the property silently retains its default. This is intentional — the validation layer (not the binder) is responsible for catching bad values via `[Range]`, `[RegularExpression]`, etc.

## Collections

Collections are bound by iterating `section.GetSection("PropertyName").GetChildren()`.

### Arrays and lists

```csharp
public string[] Hosts { get; init; } = Array.Empty<string>();
public List<int> Ports { get; init; } = new();
```

JSON:
```json
{
  "Hosts": ["host1.example.com", "host2.example.com"],
  "Ports": [5432, 5433]
}
```

Each child's `.Value` is parsed using the same strategy as scalar properties. The generator builds a `List<T>` internally and calls `.ToArray()` for array properties.

**Default preservation**: If the config section is absent (no children at all), the property keeps its C#-declared default. If the section exists but is empty, the property gets an empty collection.

### Supported collection types

All of these resolve to `new List<T>()` at runtime:

- `T[]` (converted via `.ToArray()`)
- `List<T>`
- `IList<T>`
- `ICollection<T>`
- `IEnumerable<T>`
- `IReadOnlyList<T>`
- `IReadOnlyCollection<T>`

### Dictionaries

```csharp
public Dictionary<string, string> Headers { get; init; } = new();
```

JSON:
```json
{
  "Headers": {
    "X-Api-Key": "secret",
    "Content-Type": "application/json"
  }
}
```

Each child's `.Key` becomes the dictionary key; `.Value` is parsed as the value type. Only `string` keys are supported (this is a fundamental limitation of `IConfiguration` — section child keys are always strings).

Supported dictionary types:
- `Dictionary<string, T>`
- `IDictionary<string, T>`
- `IReadOnlyDictionary<string, T>`

### Element types

Collection elements can be any scalar type from the supported set: `string`, all numerics, `bool`, `Guid`, `TimeSpan`, `DateTime(Offset)`, `Uri`, and enums. Collections whose element type is itself a `[ConfigSection]`-annotated record are also supported — see [Collections of nested config types](#collections-of-nested-config-types) below.

### Collections of nested config types

`List<T>` / `T[]` / `IReadOnlyList<T>` (and the other list-like interfaces) work when `T` is itself `[ConfigSection]`-annotated. Each child section is passed to `T`'s generated `(IConfigurationSection)` constructor, and every element is validated via its own generated `Validator` as part of the parent's validation pass.

```csharp
[ConfigSection("Api")]
public partial record ApiConfig
{
    public List<EndpointConfig> Endpoints { get; init; } = new();
}

[ConfigSection("__endpoint__")]  // section name doesn't matter for element types
public partial record EndpointConfig
{
    public string Url { get; init; } = default!;
    public TimeSpan Timeout { get; init; }
}
```

JSON:
```json
{
  "Api": {
    "Endpoints": [
      { "Url": "https://primary.example/",  "Timeout": "00:00:10" },
      { "Url": "https://replica.example/",  "Timeout": "00:00:20" }
    ]
  }
}
```

`[MinLength]` and `[MaxLength]` work on these collections by checking `.Count` / `.Length`. If the `Endpoints` section is absent from config, the user-declared default (e.g. `new()`) is preserved.

### Unsupported collections

These produce a `CB0010` warning and are skipped:

- `HashSet<T>`, `SortedSet<T>` — use `List<T>` and deduplicate in a `ValidateCustom` hook
- `Dictionary<TKey, T>` where `TKey` is not `string` — IConfiguration keys are always strings
- `Dictionary<string, ComplexType>` where `ComplexType` is `[ConfigSection]`-annotated — tracked for a future release (use `List<ComplexType>` today)
- Nested collections (`T[][]`, `List<List<T>>`)
- Immutable collections (`ImmutableArray<T>`, `FrozenSet<T>`)

## Nested config types

A property whose type is another `[ConfigSection]`-annotated record is bound recursively:

```csharp
[ConfigSection("Database")]
public partial record DatabaseConfig
{
    public string ConnectionString { get; init; } = default!;
    public RetryConfig Retry { get; init; } = default!;
}

[ConfigSection("__unused__")]  // section name doesn't matter for nested types
public partial record RetryConfig
{
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan Backoff { get; init; }
}
```

JSON:
```json
{
  "Database": {
    "ConnectionString": "Server=...",
    "Retry": {
      "MaxAttempts": 5,
      "Backoff": "00:00:02"
    }
  }
}
```

The generated constructor calls:
```csharp
this.Retry = new RetryConfig(section.GetSection("Retry"));
```

The inner type's `[ConfigSection]` attribute is required so it gets its own generated constructor, but the section name on nested types is irrelevant — it's only used when the type is registered directly via `services.AddRetryConfig(configuration)`. For nested usage, the parent drives the binding.

### Absent nested sections

If the `"Retry"` section is absent from config, the property retains its default. If the property is non-nullable and the section is absent, validation will catch it with a null-check error.

## Unsupported types

Any property type not in the supported set produces a **CB0010** warning at build time and a `// Skipped` comment in the generated constructor. The property keeps its C#-declared default at runtime.

To add support for a custom type, wrap it in a `[ConfigSection]`-annotated record and use it as a nested config type.
