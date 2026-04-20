// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using ConfigBoundNET;
using ConfigBoundNET.AotTests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

// ─────────────────────────────────────────────────────────────────────────────
// AOT smoke test
//
// This program is the runtime half of the AOT smoke test. The static half is
// the build itself: with <IsAotCompatible>true</IsAotCompatible> in the csproj
// and <TreatWarningsAsErrors>true</TreatWarningsAsErrors> inherited from
// Directory.Build.props, any IL2026 / IL3050 / IL2070 warning the trim or AOT
// analyzers raise on generated code immediately fails compilation.
//
// At runtime, this program builds an IConfiguration from appsettings.json,
// runs the generated AddDbConfig extension, resolves IOptions<DbConfig>, and
// asserts every property matches the expected value. Any mismatch returns a
// non-zero exit code so CI flags the regression even if the build passed.
//
// We deliberately use the bare ConfigurationBuilder + ServiceCollection
// pattern instead of Host.CreateApplicationBuilder. The Generic Host pulls in
// pieces (logging, host lifetime, signal handlers) that are unrelated to the
// thing we want to test, and any one of them could trigger an AOT warning
// that has nothing to do with ConfigBoundNET.
// ─────────────────────────────────────────────────────────────────────────────

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .Build();

var services = new ServiceCollection();
// Exercise the assembly-wide aggregate extension instead of calling
// services.AddDbConfig(configuration) directly — this proves the generated
// AddConfigBoundSections path is AOT-clean (no IL2026 / IL3050 warnings
// under IsAotCompatible=true + TreatWarningsAsErrors).
services.AddConfigBoundSections(configuration);

using var sp = services.BuildServiceProvider();
var db = sp.GetRequiredService<IOptions<DbConfig>>().Value;

// ── Assertions. We track failures rather than throwing on the first one
//    so a single CI run reports every regression at once. ──────────────────
var failures = new List<string>();

Check(failures, nameof(db.Conn), db.Conn, "Server=localhost;Database=AotSmoke;Trusted_Connection=True;");
Check(failures, nameof(db.Port), db.Port, 5432);
Check(failures, nameof(db.MaxBytes), db.MaxBytes, 9_999_999_999L);
Check(failures, nameof(db.Retries), db.Retries, (byte)7);
Check(failures, nameof(db.Ratio), db.Ratio, 0.75);
Check(failures, nameof(db.Price), db.Price, 19.95m);
Check(failures, nameof(db.UseSsl), db.UseSsl, true);
Check(failures, nameof(db.TenantId), db.TenantId, Guid.Parse("0c4f1b9a-7e3a-4b2d-9a8e-1c2d3e4f5a6b"));
Check(failures, nameof(db.Timeout), db.Timeout, TimeSpan.FromSeconds(90));
Check(failures, nameof(db.DeployedAt), db.DeployedAt, new DateTimeOffset(2026, 4, 10, 8, 30, 0, TimeSpan.Zero));
Check(failures, nameof(db.Endpoint), db.Endpoint, new Uri("https://example.com/api"));
Check(failures, nameof(db.MinLevel), db.MinLevel, LogLevel.Warning);

// Nested config: confirm both inner properties round-tripped through the
// recursive (IConfigurationSection) constructor.
Check(failures, "Retry.MaxAttempts", db.Retry.MaxAttempts, 5);
Check(failures, "Retry.Backoff", db.Retry.Backoff, TimeSpan.FromSeconds(2));

// Collection binding: confirm arrays are bound from indexed children.
Check(failures, "Tags.Length", db.Tags.Length, 2);
Check(failures, "Tags[0]", db.Tags[0], "primary");
Check(failures, "Tags[1]", db.Tags[1], "replica");

// Collection-of-[ConfigSection] binding: each child section is passed to
// EndpointConfig's generated (IConfigurationSection) constructor. This is the
// path that was CB0010 before the NestedConfigCollection strategy shipped.
Check(failures, "Endpoints.Count", db.Endpoints.Count, 2);
Check(failures, "Endpoints[0].Url", db.Endpoints[0].Url, "https://primary.example/");
Check(failures, "Endpoints[0].Timeout", db.Endpoints[0].Timeout, TimeSpan.FromSeconds(10));
Check(failures, "Endpoints[1].Url", db.Endpoints[1].Url, "https://replica.example/");
Check(failures, "Endpoints[1].Timeout", db.Endpoints[1].Timeout, TimeSpan.FromSeconds(20));

// Dictionary-of-[ConfigSection] binding: each child section's Key becomes
// the dictionary key, and its body is passed to EndpointConfig's generated
// constructor. Exercises the NestedConfigDictionary strategy.
Check(failures, "Tenants.Count", db.Tenants.Count, 2);
Check(failures, "Tenants[acme].Url", db.Tenants["acme"].Url, "https://acme.example/");
Check(failures, "Tenants[acme].Timeout", db.Tenants["acme"].Timeout, TimeSpan.FromSeconds(5));
Check(failures, "Tenants[globex].Url", db.Tenants["globex"].Url, "https://globex.example/");
Check(failures, "Tenants[globex].Timeout", db.Tenants["globex"].Timeout, TimeSpan.FromSeconds(15));

// ── JSON-schema const. The generator emits ConfigBoundJsonSchema.Json as
//    a verbatim-string const describing every [ConfigSection] in the
//    assembly. Parse it with JsonDocument (AOT-safe, no reflection) and
//    assert the top-level "Db" property exists with an "object" shape.
//    Catches regressions where the schema pipeline fails to emit or the
//    wrapper class's name/namespace drifts. ────────────────────────────────
try
{
    using var schemaDoc = JsonDocument.Parse(ConfigBoundJsonSchema.Json);
    var root = schemaDoc.RootElement;
    var dbSchema = root.GetProperty("properties").GetProperty("Db");
    if (dbSchema.GetProperty("type").GetString() != "object")
    {
        failures.Add("ConfigBoundJsonSchema.Json: top-level Db section did not have type=object.");
    }
}
catch (JsonException ex)
{
    failures.Add("ConfigBoundJsonSchema.Json: failed to parse as JSON — " + ex.Message);
}

// ── Report. ────────────────────────────────────────────────────────────────
if (failures.Count == 0)
{
    Console.WriteLine("[AOT smoke] OK — every supported BindingStrategy round-tripped through the generated binder.");
    return 0;
}

Console.Error.WriteLine($"[AOT smoke] FAIL — {failures.Count} property/properties did not bind as expected:");
foreach (var line in failures)
{
    Console.Error.WriteLine("  - " + line);
}
return 1;

// ── Helpers. ───────────────────────────────────────────────────────────────
//
// Local helper kept at top-level so the rest of Program.cs reads as a flat
// script. Boxing through `object?` is fine here: the failure list is a
// diagnostic path, not a hot loop.
static void Check<T>(List<string> failures, string name, T actual, T expected)
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected))
    {
        failures.Add($"{name}: expected '{expected}', got '{actual}'");
    }
}

namespace ConfigBoundNET.AotTests
{
    /// <summary>
    /// Enum used by <see cref="DbConfig.MinLevel"/>. Declared at namespace
    /// scope so the generator's enum binder can resolve it via
    /// <c>Enum.TryParse&lt;T&gt;</c>, which is the AOT-safe parse path.
    /// Marked <c>internal</c> because this is an executable, not a library
    /// (CA1515 — public surface in apps is meaningless).
    /// </summary>
    internal enum LogLevel
    {
        Trace,
        Debug,
        Information,
        Warning,
        Error,
    }

    /// <summary>
    /// Single config record that touches every <c>BindingStrategy</c> branch
    /// the generator currently supports. Adding a new property to this type
    /// is the canonical way to extend the smoke test when a new strategy
    /// lands.
    /// </summary>
    /// <remarks>
    /// The non-nullable string property (<see cref="Conn"/>) doubles as the
    /// validator-required smoke check: if the binder fails to populate it,
    /// the generated <c>DbConfig.Validator</c> will fail at startup before we
    /// even reach the assertion loop in <c>Main</c>.
    /// </remarks>
    [ConfigSection("Db")]
    internal partial record DbConfig
    {
        /// <summary>Required string. Validator catches missing values.</summary>
        public string Conn { get; init; } = default!;

        /// <summary>
        /// Port with a <c>[Range]</c> annotation. Exercises the generated
        /// bounds-check emitter + the numeric literal formatter under AOT.
        /// </summary>
        [Range(1, 65535)]
        public int Port { get; init; }

        public long MaxBytes { get; init; }
        public byte Retries { get; init; }

        public double Ratio { get; init; }
        public decimal Price { get; init; }

        public bool UseSsl { get; init; }

        public Guid TenantId { get; init; }
        public TimeSpan Timeout { get; init; }
        public DateTimeOffset DeployedAt { get; init; }

        public Uri Endpoint { get; init; } = default!;

        public LogLevel MinLevel { get; init; }

        /// <summary>
        /// String array. Exercises the collection binder under AOT:
        /// <c>section.GetSection("Tags").GetChildren()</c> iteration +
        /// <c>.ToArray()</c>.
        /// </summary>
        public string[] Tags { get; init; } = System.Array.Empty<string>();

        /// <summary>
        /// Nested complex type. The classifier returns
        /// <c>BindingStrategy.NestedConfig</c> only when the inner type is
        /// itself <c>[ConfigSection]</c>-annotated, so <see cref="RetryConfig"/>
        /// must be too — even though we never call <c>AddRetryConfig</c>
        /// directly. The outer constructor recurses into the inner one.
        /// </summary>
        public RetryConfig Retry { get; init; } = default!;

        /// <summary>
        /// Collection of nested complex types. The classifier returns
        /// <c>BindingStrategy.NestedConfigCollection</c> and the emitter
        /// iterates <c>section.GetChildren()</c>, passing each child to
        /// <see cref="EndpointConfig"/>'s generated constructor.
        /// </summary>
        public List<EndpointConfig> Endpoints { get; init; } = new();

        /// <summary>
        /// Dictionary of nested complex types. The classifier returns
        /// <c>BindingStrategy.NestedConfigDictionary</c> and the emitter
        /// iterates <c>section.GetChildren()</c>, using each child's
        /// <c>Key</c> as the dictionary key and passing its body to
        /// <see cref="EndpointConfig"/>'s generated constructor.
        /// </summary>
        public Dictionary<string, EndpointConfig> Tenants { get; init; } = new();
    }

    /// <summary>
    /// Nested config record. The section name is intentionally meaningless:
    /// only the outer <see cref="DbConfig"/> ever calls
    /// <c>AddDbConfig(configuration)</c>; the inner type's section is
    /// reached via the recursive constructor, not via an
    /// <c>IConfiguration.GetSection("__retry__")</c> call.
    /// </summary>
    [ConfigSection("__retry__")]
    internal partial record RetryConfig
    {
        public int MaxAttempts { get; init; }
        public TimeSpan Backoff { get; init; }
    }

    /// <summary>
    /// Element type for the <see cref="DbConfig.Endpoints"/> collection. Must
    /// carry <c>[ConfigSection]</c> so the classifier recognises the outer
    /// <c>List&lt;EndpointConfig&gt;</c> as <c>NestedConfigCollection</c>.
    /// </summary>
    [ConfigSection("__endpoint__")]
    internal partial record EndpointConfig
    {
        public string Url { get; init; } = default!;
        public TimeSpan Timeout { get; init; }
    }
}
