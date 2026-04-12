# Migrating from Manual Options Binding

This guide walks through converting a typical hand-written `IOptions<T>` setup to ConfigBoundNET.

## Before: the standard pattern

```csharp
// DbConfig.cs
public class DbConfig
{
    public string ConnectionString { get; set; } = "";
    public int CommandTimeoutSeconds { get; set; } = 30;
}

// DbConfigValidator.cs
public class DbConfigValidator : IValidateOptions<DbConfig>
{
    public ValidateOptionsResult Validate(string? name, DbConfig options)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            return ValidateOptionsResult.Fail("ConnectionString is required.");

        if (options.CommandTimeoutSeconds < 1 || options.CommandTimeoutSeconds > 300)
            return ValidateOptionsResult.Fail("CommandTimeoutSeconds must be between 1 and 300.");

        return ValidateOptionsResult.Success;
    }
}

// Program.cs
services.Configure<DbConfig>(configuration.GetSection("Database"));
services.AddSingleton<IValidateOptions<DbConfig>, DbConfigValidator>();
services.AddOptions<DbConfig>().ValidateOnStart();
```

**Problems with this approach:**
- The validator is hand-written boilerplate that duplicates what the type already expresses
- The section name `"Database"` is a magic string — typo it and you get empty defaults with no error
- `Configure<T>(IConfiguration)` uses `ConfigurationBinder.Bind` internally — reflection-based, not AOT-safe
- Adding a new property requires updating both the class and the validator

## After: with ConfigBoundNET

```csharp
// DbConfig.cs
[ConfigSection("Database")]
public partial record DbConfig
{
    public string ConnectionString { get; init; } = default!;

    [Range(1, 300)]
    public int CommandTimeoutSeconds { get; init; } = 30;
}

// Program.cs
builder.Services
    .AddDatabaseConfig(builder.Configuration)
    .AddOptions<DbConfig>()
    .ValidateOnStart();
```

**What changed:**
1. `class` → `partial record` (or `partial class` — both work)
2. `set;` → `init;` (recommended for immutable config, but `set;` works too)
3. Added `[ConfigSection("Database")]` — the generator takes over from here
4. Deleted `DbConfigValidator.cs` entirely — the generator produces it
5. Replaced `services.Configure<DbConfig>(...)` with `services.AddDatabaseConfig(...)`
6. Moved the range check from hand-written code to `[Range(1, 300)]`

## Step-by-step migration

### Step 1: Add the package

```bash
dotnet add package ConfigBoundNET
```

### Step 2: Add `partial` and `[ConfigSection]`

```diff
+ using ConfigBoundNET;
+
+ [ConfigSection("Database")]
- public class DbConfig
+ public partial record DbConfig
  {
-     public string ConnectionString { get; set; } = "";
+     public string ConnectionString { get; init; } = default!;
-     public int CommandTimeoutSeconds { get; set; } = 30;
+     public int CommandTimeoutSeconds { get; init; } = 30;
  }
```

If you forget `partial`, you get CB0001 at build time with a one-click fix.

### Step 3: Add DataAnnotations (optional)

Replace hand-written validation with attributes:

```diff
+ using System.ComponentModel.DataAnnotations;
+
  [ConfigSection("Database")]
  public partial record DbConfig
  {
      public string ConnectionString { get; init; } = default!;

+     [Range(1, 300)]
      public int CommandTimeoutSeconds { get; init; } = 30;
  }
```

### Step 4: Delete the hand-written validator

Delete `DbConfigValidator.cs`. The generator produces `DbConfig.Validator` with equivalent checks.

### Step 5: Update Program.cs

```diff
- services.Configure<DbConfig>(configuration.GetSection("Database"));
- services.AddSingleton<IValidateOptions<DbConfig>, DbConfigValidator>();
+ builder.Services.AddDatabaseConfig(builder.Configuration);
  builder.Services.AddOptions<DbConfig>().ValidateOnStart();
```

### Step 6: Move cross-field rules to ValidateCustom

If your hand-written validator had cross-field rules, implement them in the partial method:

```csharp
[ConfigSection("Database")]
public partial record DbConfig
{
    public string? ConnectionString { get; init; }
    public string? ConnectionStringRef { get; init; }

    partial void ValidateCustom(List<string> failures)
    {
        if (ConnectionString is null && ConnectionStringRef is null)
            failures.Add("[Database] Either ConnectionString or ConnectionStringRef must be set.");
    }
}
```

## Property accessor migration

| Before | After | Notes |
|---|---|---|
| `{ get; set; }` | `{ get; init; }` | Recommended: immutable config, AOT-safe binding from constructor |
| `{ get; set; }` | `{ get; set; }` | Also works: `partial class` with `set;` is fully supported |
| `= ""` | `= default!` | For required strings: `default!` triggers the null-suppression warning if unbound, and the validator catches it |
| `= 0` | (no initializer) | For required ints: the default `0` is fine; add `[Range]` to validate bounds |

## What you get after migration

- **Build-time diagnostics**: typos in the section name, missing `partial`, misapplied annotations
- **AOT safety**: no `ConfigurationBinder.Bind`, no reflection
- **Reload support**: `IOptionsMonitor<T>` still works (change tokens are wired automatically)
- **IDE code fixes**: lightbulb actions for common mistakes
- **Snapshot-testable output**: pin the generated code with Verify to catch unintended changes
