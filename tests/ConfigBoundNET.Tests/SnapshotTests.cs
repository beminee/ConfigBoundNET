// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using VerifyTests;
using Xunit;

namespace ConfigBoundNET.Tests;

/// <summary>
/// Snapshot tests that pin the exact generated output for representative
/// scenarios. Any change to the emitter surfaces as a diff in the
/// <c>.verified.cs</c> files under the <c>Snapshots/</c> directory,
/// which must be explicitly accepted before the test passes again.
/// </summary>
public sealed class SnapshotTests
{
    static SnapshotTests()
    {
        VerifySourceGenerators.Initialize();
    }

    private static SettingsTask VerifyDriver(
        string source,
        [CallerFilePath] string sourceFile = "",
        [CallerMemberName] string member = "")
    {
        var driver = GeneratorHarness.CreateDriver(source);
        return Verifier.Verify(driver, sourceFile: sourceFile)
            .UseDirectory("Snapshots")
            .UseMethodName(member);
    }

    // ── Core generation ──────────────────────────────────────────────────

    [Fact]
    public Task Basic_record_with_string_and_int()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Db")]
            public partial record DbConfig
            {
                public string Conn { get; init; } = default!;
                public int CommandTimeoutSeconds { get; init; } = 30;
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task Parameterless_attribute_infers_section_name()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection]
            public partial record PaymentsConfig
            {
                public string ApiKey { get; init; } = default!;
                public decimal Amount { get; init; }
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task Class_instead_of_record()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Cache")]
            public partial class CacheConfig
            {
                public string Host { get; set; } = default!;
                public int Port { get; set; } = 6379;
                public bool UseSsl { get; set; }
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task Global_namespace_no_namespace_declaration()
    {
        const string Source = """
            using ConfigBoundNET;

            [ConfigSection("App")]
            public partial record AppConfig
            {
                public string Name { get; init; } = default!;
            }
            """;

        return VerifyDriver(Source);
    }

    // ── Property types ───────────────────────────────────────────────────

    [Fact]
    public Task All_primitive_types()
    {
        const string Source = """
            using ConfigBoundNET;
            using System;

            namespace MyApp;

            [ConfigSection("Types")]
            public partial record TypesConfig
            {
                public string Text { get; init; } = default!;
                public bool Enabled { get; init; }
                public byte SmallNum { get; init; }
                public int Count { get; init; }
                public long BigNum { get; init; }
                public double Ratio { get; init; }
                public decimal Price { get; init; }
                public Guid TenantId { get; init; }
                public TimeSpan Timeout { get; init; }
                public DateTime CreatedAt { get; init; }
                public DateTimeOffset DeployedAt { get; init; }
                public Uri Endpoint { get; init; } = default!;
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task Enum_property()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            public enum LogLevel { Trace, Debug, Info, Warn, Error }

            [ConfigSection("Log")]
            public partial record LogConfig
            {
                public LogLevel MinLevel { get; init; }
                public string? OutputPath { get; init; }
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task Nullable_and_optional_properties()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Opt")]
            public partial record OptionalConfig
            {
                public string Required { get; init; } = default!;
                public string? OptionalString { get; init; }
                public int? OptionalInt { get; init; }
                public bool? OptionalBool { get; init; }
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task Nested_config_type()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Db")]
            public partial record DbConfig
            {
                public string Conn { get; init; } = default!;
                public RetryPolicy Retry { get; init; } = default!;
            }

            [ConfigSection("__nested__")]
            public partial record RetryPolicy
            {
                public int MaxAttempts { get; init; } = 3;
                public bool Enabled { get; init; }
            }
            """;

        return VerifyDriver(Source);
    }

    // ── DataAnnotations ──────────────────────────────────────────────────

    [Fact]
    public Task Range_and_StringLength_annotations()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Api")]
            public partial record ApiConfig
            {
                [Range(1, 65535)]
                public int Port { get; init; } = 8080;

                [StringLength(200, MinimumLength = 5)]
                public string DisplayName { get; init; } = default!;
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task RegularExpression_annotation()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Web")]
            public partial record WebConfig
            {
                [RegularExpression(@"^https?://")]
                public string Endpoint { get; init; } = default!;
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task Url_and_EmailAddress_annotations()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Contact")]
            public partial record ContactConfig
            {
                [Url]
                public string Website { get; init; } = default!;

                [EmailAddress]
                public string AdminEmail { get; init; } = default!;
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task MinLength_and_MaxLength_annotations()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Auth")]
            public partial record AuthConfig
            {
                [MinLength(32)]
                public string Secret { get; init; } = default!;

                [MaxLength(100)]
                public string Issuer { get; init; } = default!;
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task Multiple_annotations_on_same_property()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Pwd")]
            public partial record PasswordConfig
            {
                [MinLength(8)]
                [MaxLength(128)]
                [RegularExpression(@"^(?=.*[A-Z])(?=.*[0-9])")]
                public string DefaultPassword { get; init; } = default!;
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task Annotation_with_custom_ErrorMessage()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Test")]
            public partial record TestConfig
            {
                [Range(1, 65535, ErrorMessage = "Port {0} must be between {1} and {2}.")]
                public int Port { get; init; } = 8080;

                [RegularExpression(@"^https?://", ErrorMessage = "Must be an http(s) URL.")]
                public string Endpoint { get; init; } = default!;
            }
            """;

        return VerifyDriver(Source);
    }

    // ── Collections ───────────────────────────────────────────────────────

    [Fact]
    public Task Array_and_List_and_Dictionary()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.Collections.Generic;

            namespace MyApp;

            [ConfigSection("Net")]
            public partial record NetConfig
            {
                public string[] Hosts { get; init; } = System.Array.Empty<string>();
                public List<int> Ports { get; init; } = new();
                public Dictionary<string, string> Headers { get; init; } = new();
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task NestedConfig_list()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.Collections.Generic;

            namespace MyApp;

            [ConfigSection("Api")]
            public partial record ApiConfig
            {
                public List<EndpointConfig> Endpoints { get; init; } = new();
            }

            [ConfigSection("__endpoint__")]
            public partial record EndpointConfig
            {
                public string Url { get; init; } = default!;
                public System.TimeSpan Timeout { get; init; }
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task NestedConfig_array()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Api")]
            public partial record ApiConfig
            {
                public EndpointConfig[] Endpoints { get; init; } = System.Array.Empty<EndpointConfig>();
            }

            [ConfigSection("__endpoint__")]
            public partial record EndpointConfig
            {
                public string Url { get; init; } = default!;
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task NestedConfig_readonly_list()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.Collections.Generic;

            namespace MyApp;

            [ConfigSection("Api")]
            public partial record ApiConfig
            {
                public IReadOnlyList<EndpointConfig> Endpoints { get; init; } = new List<EndpointConfig>();
            }

            [ConfigSection("__endpoint__")]
            public partial record EndpointConfig
            {
                public string Url { get; init; } = default!;
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task NestedConfig_list_with_nullable_element()
    {
        // Pins the codegen for List<EndpointConfig?>. The emitted container
        // preserves the ? annotation (so assignment type-checks under strict
        // nullable) while the constructor invocation strips it (since
        // `new Foo?(x)` isn't valid C#). The validator null-guards each
        // element defensively even though the binder never produces nulls.
        const string Source = """
            using ConfigBoundNET;
            using System.Collections.Generic;

            #nullable enable

            namespace MyApp;

            [ConfigSection("Api")]
            public partial record ApiConfig
            {
                public List<EndpointConfig?> Endpoints { get; init; } = new();
            }

            [ConfigSection("__endpoint__")]
            public partial record EndpointConfig
            {
                public string Url { get; init; } = default!;
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task NestedConfig_dictionary()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.Collections.Generic;

            namespace MyApp;

            [ConfigSection("Api")]
            public partial record ApiConfig
            {
                public Dictionary<string, TenantConfig> Tenants { get; init; } = new();
            }

            [ConfigSection("__tenant__")]
            public partial record TenantConfig
            {
                public string BaseUrl { get; init; } = default!;
                public System.TimeSpan Timeout { get; init; }
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task NestedConfig_readonly_dictionary()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.Collections.Generic;

            namespace MyApp;

            [ConfigSection("Api")]
            public partial record ApiConfig
            {
                public IReadOnlyDictionary<string, TenantConfig> Tenants { get; init; } = new Dictionary<string, TenantConfig>();
            }

            [ConfigSection("__tenant__")]
            public partial record TenantConfig
            {
                public string BaseUrl { get; init; } = default!;
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task NestedConfig_dictionary_with_nullable_value()
    {
        // Pins the codegen for Dictionary<string, T?>. The container preserves
        // the ? annotation (so assignment type-checks under strict nullable)
        // while the constructor invocation strips it.
        const string Source = """
            using ConfigBoundNET;
            using System.Collections.Generic;

            #nullable enable

            namespace MyApp;

            [ConfigSection("Api")]
            public partial record ApiConfig
            {
                public Dictionary<string, TenantConfig?> Tenants { get; init; } = new();
            }

            [ConfigSection("__tenant__")]
            public partial record TenantConfig
            {
                public string BaseUrl { get; init; } = default!;
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task NestedConfig_list_with_inner_annotations()
    {
        // Inner element carries DataAnnotations. The parent's Validator.Validate
        // must recurse into each element so inner failures surface in the
        // parent's result.
        const string Source = """
            using ConfigBoundNET;
            using System.Collections.Generic;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Api")]
            public partial record ApiConfig
            {
                [MinLength(1)]
                public List<EndpointConfig> Endpoints { get; init; } = new();
            }

            [ConfigSection("__endpoint__")]
            public partial record EndpointConfig
            {
                public string Url { get; init; } = default!;

                [Range(1, 65535)]
                public int Port { get; init; } = 80;
            }
            """;

        return VerifyDriver(Source);
    }

    // ── Aggregate registration ───────────────────────────────────────────

    [Fact]
    public Task AggregateRegistration_multiple_types()
    {
        // Two [ConfigSection] types in different namespaces. The aggregate
        // emitter must emit a single AddConfigBoundSections file in
        // ConfigBoundNET namespace, with guarded calls for both types in
        // deterministic alphabetical order.
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp.Api
            {
                [ConfigSection("Api")]
                public partial record ApiConfig
                {
                    public string BaseUrl { get; init; } = default!;
                }
            }

            namespace MyApp.Db
            {
                [ConfigSection("Db")]
                public partial record DbConfig
                {
                    public string Conn { get; init; } = default!;
                }
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task AggregateRegistration_zero_types()
    {
        // Compilation with no [ConfigSection] types — the aggregate method
        // should still be emitted with an empty body so callers can always
        // write services.AddConfigBoundSections(config) unconditionally.
        const string Source = """
            namespace MyApp;

            public sealed class NotAConfigType
            {
                public int Value { get; set; }
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task AggregateRegistration_with_global_namespace_type()
    {
        // A type in the global namespace must emit "global::TypeName" without
        // a leading "global::Namespace.". Pins the namespace-null branch of
        // the emitter.
        const string Source = """
            using ConfigBoundNET;

            [ConfigSection("App")]
            public partial record AppConfig
            {
                public string Name { get; init; } = default!;
            }
            """;

        return VerifyDriver(Source);
    }

    // ── [Sensitive] redaction ────────────────────────────────────────────

    [Fact]
    public Task Sensitive_attribute_on_record()
    {
        // Pins the record-shaped PrintMembers override. A reference-type
        // sensitive property gets the null-safe ternary; a value-type one
        // renders unconditionally as "***"; a non-sensitive property is
        // passed through via `builder.Append(this.X)`.
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Db")]
            public partial record DbConfig
            {
                [Sensitive]
                public string Conn { get; init; } = default!;

                [Sensitive]
                public System.Guid Token { get; init; }

                public int Port { get; init; } = 5432;
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task Sensitive_attribute_on_class()
    {
        // Pins the class-shaped ToString override. Classes don't get a
        // compiler-synthesized PrintMembers, so the emitter produces the
        // whole `"TypeName { ... }"` wrapper itself.
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Cache")]
            public partial class CacheConfig
            {
                [Sensitive]
                public string Secret { get; set; } = default!;

                public int Port { get; set; } = 6379;
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task Sensitive_attribute_absent_emits_no_redacted_override()
    {
        // Without any [Sensitive] property, the emitter must emit no
        // PrintMembers / ToString override — the compiler's default record
        // behaviour handles the common case, and emitting an override for
        // every type would break users who've hand-written their own.
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Plain")]
            public partial record PlainConfig
            {
                public string Conn { get; init; } = default!;
                public int Port { get; init; } = 5432;
            }
            """;

        return VerifyDriver(Source);
    }

    // ── Custom validation hook ───────────────────────────────────────────

    [Fact]
    public Task ValidateCustom_hook_is_emitted()
    {
        // The hook declaration should appear even when no properties exist.
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Empty")]
            public partial record EmptyConfig;
            """;

        return VerifyDriver(Source);
    }
}
