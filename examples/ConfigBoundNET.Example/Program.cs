// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using ConfigBoundNET;
using ConfigBoundNET.Example;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

// ─────────────────────────────────────────────────────────────────────────────
// 1. Build a host using the standard Generic Host pattern.
//    CreateApplicationBuilder wires up appsettings.json, environment variables,
//    and command line arguments into builder.Configuration automatically.
// ─────────────────────────────────────────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);

// ─────────────────────────────────────────────────────────────────────────────
// 2. Register every [ConfigSection]-annotated type in this assembly in a
//    single call via the generator-emitted aggregate extension. Chaining
//    validateOnStart: true is equivalent to calling
//    services.AddOptions<T>().ValidateOnStart() for every registered section —
//    so misconfiguration crashes the host during StartAsync rather than on
//    the first IOptions<T>.Value read at request time. Try blanking out
//    "Db.Conn" in appsettings.json and re-running to see it in action.
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddConfigBoundSections(builder.Configuration, validateOnStart: true);

using var app = builder.Build();

// ─────────────────────────────────────────────────────────────────────────────
// 3. Start the host. Because step 2 passed validateOnStart: true, any
//    [Required] / nullability / [Range] / ValidateCustom failure throws an
//    OptionsValidationException out of StartAsync — BEFORE we read
//    IOptions<T>.Value below. Without the flag, the same validation would
//    still run, but only on the first .Value access later, so bad config
//    could hide until a cold-path request triggers resolution.
// ─────────────────────────────────────────────────────────────────────────────
await app.StartAsync().ConfigureAwait(false);

// ─────────────────────────────────────────────────────────────────────────────
// 4. Resolve the fully validated instance and use it.
// ─────────────────────────────────────────────────────────────────────────────
var dbConfig = app.Services.GetRequiredService<IOptions<DbConfig>>().Value;

System.Console.WriteLine($"[{DbConfig.SectionName}] Conn                   = {dbConfig.Conn}");
System.Console.WriteLine($"[{DbConfig.SectionName}] CommandTimeoutSeconds  = {dbConfig.CommandTimeoutSeconds}");
System.Console.WriteLine($"[{DbConfig.SectionName}] ReplicaConn            = {dbConfig.ReplicaConn ?? "(not set)"}");
System.Console.WriteLine();
System.Console.WriteLine("Validation passed — all cross-field rules satisfied.");
System.Console.WriteLine();

// ─────────────────────────────────────────────────────────────────────────────
// 4. Demonstrate [Sensitive] redaction. Direct property access (above) still
//    returns the raw value — that's by design; your app code needs the real
//    secret to open a connection. But anywhere the config is dumped as a
//    whole — ToString(), structured-log destructurers, JSON serializers —
//    the [Sensitive] property is replaced with "***".
// ─────────────────────────────────────────────────────────────────────────────
System.Console.WriteLine("ToString()   : " + dbConfig);
System.Console.WriteLine("JSON via STJ : " + System.Text.Json.JsonSerializer.Serialize((object)dbConfig));

namespace ConfigBoundNET.Example
{
    /// <summary>
    /// Strongly-typed view of the <c>"Db"</c> configuration section.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Because this type is marked with <c>[ConfigSection("Db")]</c>, the
    /// ConfigBoundNET generator emits at build time:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>DbConfig.SectionName</c> — a compile-time constant equal to <c>"Db"</c>.</description></item>
    ///   <item><description><c>DbConfig.Validator</c> — an <c>IValidateOptions&lt;DbConfig&gt;</c> that null-checks non-nullable properties and runs DataAnnotations checks.</description></item>
    ///   <item><description><c>DbConfigServiceCollectionExtensions.AddDbConfig</c> — the DI helper called in <c>Program.cs</c>.</description></item>
    ///   <item><description><c>partial void ValidateCustom</c> — a hook for cross-field validation rules that no single-property attribute can express.</description></item>
    ///   <item><description>
    ///     A redacted <c>PrintMembers</c> override and an explicit
    ///     <c>IReadOnlyDictionary&lt;string, object?&gt;</c> implementation,
    ///     emitted because <see cref="Conn"/> is decorated with
    ///     <c>[Sensitive]</c>. See the bottom of <c>Program.cs</c> for what
    ///     this does to <c>ToString()</c> and JSON serialization output.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Non-nullable reference-type properties (like <see cref="Conn"/>) are
    /// treated as required. Making a property nullable (e.g. <c>string?</c>)
    /// opts it out of validation.
    /// </para>
    /// </remarks>
    [ConfigSection("Db")]
    internal partial record DbConfig
    {
        /// <summary>
        /// The database connection string. Required. Marked <c>[Sensitive]</c>
        /// so it's redacted in <c>ToString()</c> and in structured-logging
        /// destructurers — the generator also emits
        /// <c>IReadOnlyDictionary&lt;string, object?&gt;</c> so Serilog /
        /// System.Text.Json / MEL / NLog all see <c>"***"</c> without any
        /// logger-specific configuration.
        /// </summary>
        [Sensitive]
        public string Conn { get; init; } = default!;

        /// <summary>Command timeout in seconds. Optional; defaults to 30.</summary>
        public int CommandTimeoutSeconds { get; init; } = 30;

        /// <summary>
        /// Optional replica connection string. When set, the timeout must be
        /// at least 5 seconds because replica failover adds latency.
        /// </summary>
        public string? ReplicaConn { get; init; }

        /// <summary>
        /// Cross-field validation: if a replica is configured, enforce a
        /// minimum timeout so connections don't fail during failover.
        /// This is the kind of rule that no single-property
        /// <c>[Range]</c> or <c>[Required]</c> attribute can express.
        /// </summary>
        partial void ValidateCustom(List<string> failures)
        {
            if (ReplicaConn is not null && CommandTimeoutSeconds < 5)
            {
                failures.Add(
                    $"[{SectionName}] When ReplicaConn is set, CommandTimeoutSeconds must be >= 5 " +
                    $"(got {CommandTimeoutSeconds}). Replica failover adds latency.");
            }
        }
    }
}
