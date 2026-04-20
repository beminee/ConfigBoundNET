// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System.Linq;
using System.Text.Json;
using Xunit;

namespace ConfigBoundNET.Tests;

/// <summary>
/// Tests for the JSON Schema aggregate output emitted by
/// <see cref="ConfigBoundGenerator"/>. These assert on the JSON document
/// directly (parsed with <c>System.Text.Json</c>) rather than on the
/// surrounding C# wrapper, so a reshuffle of the emitter's whitespace or
/// comment style doesn't cause spurious test failures.
/// </summary>
/// <remarks>
/// Snapshot-style fidelity checks live alongside the existing
/// <c>SnapshotTests</c>; this file targets semantic correctness
/// (required[], enum[], format, writeOnly, nested objects, …).
/// </remarks>
public sealed class SchemaEmissionTests
{
    // Expected-value arrays extracted to static readonly fields because
    // the repo treats CA1861 (inline collection literals in method args)
    // as an error — see Directory.Build.props.
    private static readonly string[] LogLevelMembers = new[] { "Trace", "Debug", "Info", "Warn", "Error" };
    private static readonly string[] BillingPlanAllowed = new[] { "Pro", "Enterprise" };
    private static readonly string[] NetUserDenied = new[] { "admin", "root" };
    private static readonly string[] OrderedSectionNames = new[] { "Alpha", "Zulu" };

    [Fact]
    public void Emits_nothing_when_compilation_has_no_config_sections()
    {
        const string Source = """
            namespace MyApp;

            public sealed class NotAConfig
            {
                public string Value { get; set; } = string.Empty;
            }
            """;

        var result = GeneratorHarness.Run(Source);

        // Post-init trees (attributes, OptionsFactory) are always present.
        // The aggregate registration is also always emitted (empty body).
        // But the JSON schema source must NOT be added when nothing is
        // annotated, otherwise consumers would see a class holding
        // "{ ... properties: {} }" for no reason.
        Assert.DoesNotContain(
            result.GeneratedTrees,
            t => t.FilePath.EndsWith("ConfigBoundNET.JsonSchema.g.cs", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Root_document_uses_draft_2020_12_and_section_property()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Db")]
            public partial record DbConfig
            {
                public string Conn { get; init; } = default!;
            }
            """;

        var json = GeneratorHarness.Run(Source).GetEmittedSchemaJson();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(
            "https://json-schema.org/draft/2020-12/schema",
            root.GetProperty("$schema").GetString());
        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.True(root.GetProperty("additionalProperties").GetBoolean());

        var dbSection = root.GetProperty("properties").GetProperty("Db");
        Assert.Equal("object", dbSection.GetProperty("type").GetString());
        Assert.False(dbSection.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("Conn", dbSection.GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void Required_properties_populate_required_array()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Db")]
            public partial record DbConfig
            {
                public string Conn { get; init; } = default!;
                public string? ReplicaConn { get; init; }
                public int Port { get; init; } = 5432;
            }
            """;

        var json = GeneratorHarness.Run(Source).GetEmittedSchemaJson();
        using var doc = JsonDocument.Parse(json);

        var db = doc.RootElement.GetProperty("properties").GetProperty("Db");
        var required = db.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray();

        // Non-nullable ref type → required. Nullable ref and value types →
        // not required (value types bind defaults; nullable ref is explicit opt-out).
        Assert.Contains("Conn", required);
        Assert.DoesNotContain("ReplicaConn", required);
        Assert.DoesNotContain("Port", required);
    }

    [Fact]
    public void Scalar_types_and_formats_are_mapped_correctly()
    {
        const string Source = """
            using ConfigBoundNET;
            using System;

            namespace MyApp;

            [ConfigSection("Api")]
            public partial record ApiConfig
            {
                public string Name { get; init; } = default!;
                public int Count { get; init; }
                public double Ratio { get; init; }
                public bool Enabled { get; init; }
                public Guid TenantId { get; init; }
                public DateTime StartedAt { get; init; }
                public Uri Endpoint { get; init; } = default!;
                public TimeSpan Timeout { get; init; }
            }
            """;

        var json = GeneratorHarness.Run(Source).GetEmittedSchemaJson();
        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.GetProperty("properties").GetProperty("Api").GetProperty("properties");

        Assert.Equal("string", props.GetProperty("Name").GetProperty("type").GetString());
        Assert.Equal("integer", props.GetProperty("Count").GetProperty("type").GetString());
        Assert.Equal("number", props.GetProperty("Ratio").GetProperty("type").GetString());
        Assert.Equal("boolean", props.GetProperty("Enabled").GetProperty("type").GetString());

        Assert.Equal("string", props.GetProperty("TenantId").GetProperty("type").GetString());
        Assert.Equal("uuid", props.GetProperty("TenantId").GetProperty("format").GetString());

        Assert.Equal("string", props.GetProperty("StartedAt").GetProperty("type").GetString());
        Assert.Equal("date-time", props.GetProperty("StartedAt").GetProperty("format").GetString());

        Assert.Equal("string", props.GetProperty("Endpoint").GetProperty("type").GetString());
        Assert.Equal("uri", props.GetProperty("Endpoint").GetProperty("format").GetString());

        // TimeSpan has no standard JSON Schema format; we emit plain string.
        Assert.Equal("string", props.GetProperty("Timeout").GetProperty("type").GetString());
        Assert.False(props.GetProperty("Timeout").TryGetProperty("format", out _));
    }

    [Fact]
    public void Nullable_value_type_emits_type_array_with_null()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Test")]
            public partial record TestConfig
            {
                public int? MaybePort { get; init; }
            }
            """;

        var json = GeneratorHarness.Run(Source).GetEmittedSchemaJson();
        using var doc = JsonDocument.Parse(json);

        var maybePort = doc.RootElement
            .GetProperty("properties").GetProperty("Test")
            .GetProperty("properties").GetProperty("MaybePort");

        var typeField = maybePort.GetProperty("type");
        Assert.Equal(JsonValueKind.Array, typeField.ValueKind);
        var types = typeField.EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("integer", types);
        Assert.Contains("null", types);
    }

    [Fact]
    public void Range_annotation_emits_minimum_and_maximum()
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
            }
            """;

        var json = GeneratorHarness.Run(Source).GetEmittedSchemaJson();
        using var doc = JsonDocument.Parse(json);

        var port = doc.RootElement
            .GetProperty("properties").GetProperty("Api")
            .GetProperty("properties").GetProperty("Port");

        Assert.Equal(1, port.GetProperty("minimum").GetInt32());
        Assert.Equal(65535, port.GetProperty("maximum").GetInt32());
    }

    [Fact]
    public void StringLength_annotation_emits_min_and_max_length_on_strings()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Api")]
            public partial record ApiConfig
            {
                [StringLength(200, MinimumLength = 5)]
                public string DisplayName { get; init; } = default!;
            }
            """;

        var json = GeneratorHarness.Run(Source).GetEmittedSchemaJson();
        using var doc = JsonDocument.Parse(json);

        var name = doc.RootElement
            .GetProperty("properties").GetProperty("Api")
            .GetProperty("properties").GetProperty("DisplayName");

        Assert.Equal(200, name.GetProperty("maxLength").GetInt32());
        Assert.Equal(5, name.GetProperty("minLength").GetInt32());
    }

    [Fact]
    public void RegularExpression_Url_Email_emit_pattern_and_formats()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Api")]
            public partial record ApiConfig
            {
                [RegularExpression(@"^https?://")]
                public string Endpoint { get; init; } = default!;

                [Url]
                public string Callback { get; init; } = default!;

                [EmailAddress]
                public string AdminEmail { get; init; } = default!;
            }
            """;

        var json = GeneratorHarness.Run(Source).GetEmittedSchemaJson();
        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement
            .GetProperty("properties").GetProperty("Api")
            .GetProperty("properties");

        Assert.Equal("^https?://", props.GetProperty("Endpoint").GetProperty("pattern").GetString());
        Assert.Equal("uri", props.GetProperty("Callback").GetProperty("format").GetString());
        Assert.Equal("email", props.GetProperty("AdminEmail").GetProperty("format").GetString());
    }

    [Fact]
    public void Enum_property_emits_member_names_as_enum_array()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            public enum LogLevel { Trace, Debug, Info, Warn, Error }

            [ConfigSection("Logging")]
            public partial record LoggingConfig
            {
                public LogLevel MinLevel { get; init; }
            }
            """;

        var json = GeneratorHarness.Run(Source).GetEmittedSchemaJson();
        using var doc = JsonDocument.Parse(json);

        var minLevel = doc.RootElement
            .GetProperty("properties").GetProperty("Logging")
            .GetProperty("properties").GetProperty("MinLevel");

        Assert.Equal("string", minLevel.GetProperty("type").GetString());
        var enumValues = minLevel.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(LogLevelMembers, enumValues);
    }

    [Fact]
    public void AllowedValues_overrides_enum_member_list()
    {
        // When [AllowedValues] is present on an enum-typed property, the
        // user is narrowing the accepted set further. The schema should
        // reflect THAT set, not the full enum.
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            public enum Tier { Free, Pro, Enterprise }

            [ConfigSection("Billing")]
            public partial record BillingConfig
            {
                [AllowedValues("Pro", "Enterprise")]
                public Tier Plan { get; init; }
            }
            """;

        var json = GeneratorHarness.Run(Source).GetEmittedSchemaJson();
        using var doc = JsonDocument.Parse(json);

        var plan = doc.RootElement
            .GetProperty("properties").GetProperty("Billing")
            .GetProperty("properties").GetProperty("Plan");

        var enumArr = plan.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(BillingPlanAllowed, enumArr);
    }

    [Fact]
    public void DeniedValues_emits_not_enum()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Net")]
            public partial record NetConfig
            {
                [DeniedValues("admin", "root")]
                public string User { get; init; } = default!;
            }
            """;

        var json = GeneratorHarness.Run(Source).GetEmittedSchemaJson();
        using var doc = JsonDocument.Parse(json);

        var user = doc.RootElement
            .GetProperty("properties").GetProperty("Net")
            .GetProperty("properties").GetProperty("User");

        var denied = user.GetProperty("not").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal(NetUserDenied, denied);
    }

    [Fact]
    public void Sensitive_property_emits_writeOnly_and_redaction_description()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Db")]
            public partial record DbConfig
            {
                [Sensitive]
                public string Conn { get; init; } = default!;
            }
            """;

        var json = GeneratorHarness.Run(Source).GetEmittedSchemaJson();
        using var doc = JsonDocument.Parse(json);

        var conn = doc.RootElement
            .GetProperty("properties").GetProperty("Db")
            .GetProperty("properties").GetProperty("Conn");

        Assert.True(conn.GetProperty("writeOnly").GetBoolean());
        Assert.Contains("Sensitive", conn.GetProperty("description").GetString());
    }

    [Fact]
    public void Nested_config_is_inlined_as_sub_object_schema()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Retry")]
            public partial record RetryConfig
            {
                public int MaxAttempts { get; init; } = 3;
            }

            [ConfigSection("Outer")]
            public partial record OuterConfig
            {
                public RetryConfig Retry { get; init; } = default!;
            }
            """;

        var json = GeneratorHarness.Run(Source).GetEmittedSchemaJson();
        using var doc = JsonDocument.Parse(json);

        var retry = doc.RootElement
            .GetProperty("properties").GetProperty("Outer")
            .GetProperty("properties").GetProperty("Retry");

        Assert.Equal("object", retry.GetProperty("type").GetString());
        Assert.Equal(
            "integer",
            retry.GetProperty("properties").GetProperty("MaxAttempts").GetProperty("type").GetString());
    }

    [Fact]
    public void Complex_collection_emits_array_with_inlined_items_schema()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.Collections.Generic;

            namespace MyApp;

            [ConfigSection("__endpoint__")]
            public partial record EndpointConfig
            {
                public string Url { get; init; } = default!;
            }

            [ConfigSection("Api")]
            public partial record ApiConfig
            {
                public List<EndpointConfig> Endpoints { get; init; } = new();
            }
            """;

        var json = GeneratorHarness.Run(Source).GetEmittedSchemaJson();
        using var doc = JsonDocument.Parse(json);

        var endpoints = doc.RootElement
            .GetProperty("properties").GetProperty("Api")
            .GetProperty("properties").GetProperty("Endpoints");

        Assert.Equal("array", endpoints.GetProperty("type").GetString());
        Assert.Equal(
            "object",
            endpoints.GetProperty("items").GetProperty("type").GetString());
        Assert.Equal(
            "string",
            endpoints.GetProperty("items").GetProperty("properties").GetProperty("Url").GetProperty("type").GetString());
    }

    [Fact]
    public void Complex_dictionary_emits_additionalProperties_schema()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.Collections.Generic;

            namespace MyApp;

            [ConfigSection("__endpoint__")]
            public partial record EndpointConfig
            {
                public string Url { get; init; } = default!;
            }

            [ConfigSection("Api")]
            public partial record ApiConfig
            {
                public Dictionary<string, EndpointConfig> Tenants { get; init; } = new();
            }
            """;

        var json = GeneratorHarness.Run(Source).GetEmittedSchemaJson();
        using var doc = JsonDocument.Parse(json);

        var tenants = doc.RootElement
            .GetProperty("properties").GetProperty("Api")
            .GetProperty("properties").GetProperty("Tenants");

        Assert.Equal("object", tenants.GetProperty("type").GetString());
        var addl = tenants.GetProperty("additionalProperties");
        Assert.Equal("object", addl.GetProperty("type").GetString());
        Assert.Equal(
            "string",
            addl.GetProperty("properties").GetProperty("Url").GetProperty("type").GetString());
    }

    [Fact]
    public void Scalar_array_emits_items_schema_for_element()
    {
        const string Source = """
            using ConfigBoundNET;
            using System;

            namespace MyApp;

            [ConfigSection("Api")]
            public partial record ApiConfig
            {
                public string[] Hosts { get; init; } = Array.Empty<string>();
            }
            """;

        var json = GeneratorHarness.Run(Source).GetEmittedSchemaJson();
        using var doc = JsonDocument.Parse(json);

        var hosts = doc.RootElement
            .GetProperty("properties").GetProperty("Api")
            .GetProperty("properties").GetProperty("Hosts");

        Assert.Equal("array", hosts.GetProperty("type").GetString());
        Assert.Equal("string", hosts.GetProperty("items").GetProperty("type").GetString());
    }

    [Fact]
    public void Collection_MinLength_MaxLength_emit_minItems_and_maxItems()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.Collections.Generic;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Api")]
            public partial record ApiConfig
            {
                [MinLength(1)]
                [MaxLength(10)]
                public List<string> Hosts { get; init; } = new();
            }
            """;

        var json = GeneratorHarness.Run(Source).GetEmittedSchemaJson();
        using var doc = JsonDocument.Parse(json);

        var hosts = doc.RootElement
            .GetProperty("properties").GetProperty("Api")
            .GetProperty("properties").GetProperty("Hosts");

        Assert.Equal(1, hosts.GetProperty("minItems").GetInt32());
        Assert.Equal(10, hosts.GetProperty("maxItems").GetInt32());
    }

    [Fact]
    public void Error_message_becomes_description()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Api")]
            public partial record ApiConfig
            {
                [RegularExpression(@"^[a-z]+$", ErrorMessage = "Lowercase only.")]
                public string Tag { get; init; } = default!;
            }
            """;

        var json = GeneratorHarness.Run(Source).GetEmittedSchemaJson();
        using var doc = JsonDocument.Parse(json);

        var tag = doc.RootElement
            .GetProperty("properties").GetProperty("Api")
            .GetProperty("properties").GetProperty("Tag");

        Assert.Equal("Lowercase only.", tag.GetProperty("description").GetString());
    }

    [Fact]
    public void Sections_are_ordered_by_hint_name()
    {
        // HintName ordering (namespace then type) gives deterministic output
        // across builds. This is relied on by any downstream diff/snapshot
        // tooling. Assert that two types emit in a stable order regardless
        // of source-file syntax-tree ordering.
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Zulu")]
            public partial record ZuluConfig
            {
                public string Value { get; init; } = default!;
            }

            [ConfigSection("Alpha")]
            public partial record AlphaConfig
            {
                public string Value { get; init; } = default!;
            }
            """;

        var json = GeneratorHarness.Run(Source).GetEmittedSchemaJson();
        using var doc = JsonDocument.Parse(json);

        var sectionNames = doc.RootElement
            .GetProperty("properties").EnumerateObject()
            .Select(p => p.Name)
            .ToArray();

        // Alpha < Zulu by both type-name and section-name — either sort
        // order would put Alpha first. The important invariant is that
        // source-order doesn't matter.
        Assert.Equal(OrderedSectionNames, sectionNames);
    }
}
