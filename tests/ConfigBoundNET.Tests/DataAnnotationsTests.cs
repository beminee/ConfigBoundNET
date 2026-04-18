// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConfigBoundNET.Tests;

/// <summary>
/// Tests for DataAnnotations validation support in the ConfigBoundNET generator.
/// Each test runs the generator against a synthetic source snippet and asserts
/// either on the emitted C# (substring checks) or on the reported diagnostics.
/// </summary>
public sealed class DataAnnotationsTests
{
    // ── Generator-output tests ───────────────────────────────────────────

    [Fact]
    public void Range_emits_bounds_check()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Test")]
            public partial record TestConfig
            {
                [Range(1, 65535)]
                public int Port { get; init; } = 5432;
            }
            """;

        var result = GeneratorHarness.Run(Source);
        var generated = result.GetEmittedConfigSource();

        Assert.Contains("options.Port < 1", generated);
        Assert.Contains("options.Port > 65535", generated);
        Assert.Contains("must be between 1 and 65535", generated);
    }

    [Fact]
    public void StringLength_emits_length_check()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Test")]
            public partial record TestConfig
            {
                [StringLength(200, MinimumLength = 5)]
                public string Name { get; init; } = default!;
            }
            """;

        var result = GeneratorHarness.Run(Source);
        var generated = result.GetEmittedConfigSource();

        Assert.Contains("options.Name.Length < 5", generated);
        Assert.Contains("options.Name.Length > 200", generated);
        Assert.Contains("must have length between 5 and 200", generated);
    }

    [Fact]
    public void MinLength_emits_minimum_check()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Test")]
            public partial record TestConfig
            {
                [MinLength(5)]
                public string Name { get; init; } = default!;
            }
            """;

        var result = GeneratorHarness.Run(Source);
        var generated = result.GetEmittedConfigSource();

        Assert.Contains("options.Name.Length < 5", generated);
        Assert.Contains("must have minimum length 5", generated);
    }

    [Fact]
    public void MaxLength_emits_maximum_check()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Test")]
            public partial record TestConfig
            {
                [MaxLength(100)]
                public string Name { get; init; } = default!;
            }
            """;

        var result = GeneratorHarness.Run(Source);
        var generated = result.GetEmittedConfigSource();

        Assert.Contains("options.Name.Length > 100", generated);
        Assert.Contains("must have maximum length 100", generated);
    }

    [Fact]
    public void RegularExpression_emits_regex_field_and_match()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Test")]
            public partial record TestConfig
            {
                [RegularExpression(@"^https?://")]
                public string Endpoint { get; init; } = default!;
            }
            """;

        var result = GeneratorHarness.Run(Source);
        var generated = result.GetEmittedConfigSource();

        Assert.Contains("private static readonly", generated);
        Assert.Contains("_cb_Regex_Endpoint", generated);
        Assert.Contains("RegexOptions.Compiled", generated);
        Assert.Contains("IsMatch(options.Endpoint)", generated);
        Assert.Contains("does not match the required pattern", generated);
    }

    [Fact]
    public void Url_emits_TryCreate_check()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Test")]
            public partial record TestConfig
            {
                [Url]
                public string Endpoint { get; init; } = default!;
            }
            """;

        var result = GeneratorHarness.Run(Source);
        var generated = result.GetEmittedConfigSource();

        Assert.Contains("Uri.TryCreate(options.Endpoint", generated);
        Assert.Contains("UriKind.Absolute", generated);
        Assert.Contains("is not a valid absolute URL", generated);
    }

    [Fact]
    public void EmailAddress_emits_regex_check()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Test")]
            public partial record TestConfig
            {
                [EmailAddress]
                public string Email { get; init; } = default!;
            }
            """;

        var result = GeneratorHarness.Run(Source);
        var generated = result.GetEmittedConfigSource();

        Assert.Contains("_cb_Regex_Email_Email", generated);
        Assert.Contains("IsMatch(options.Email)", generated);
        Assert.Contains("is not a valid email address", generated);
    }

    [Fact]
    public void Multiple_annotations_on_same_property_all_emit()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Test")]
            public partial record TestConfig
            {
                [MinLength(3)]
                [MaxLength(50)]
                public string Name { get; init; } = default!;
            }
            """;

        var result = GeneratorHarness.Run(Source);
        var generated = result.GetEmittedConfigSource();

        Assert.Contains("options.Name.Length < 3", generated);
        Assert.Contains("options.Name.Length > 50", generated);
    }

    // ── Length on collections of complex elements ───────────────────────

    [Fact]
    public void MinLength_on_nested_config_list_emits_count_check()
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
                public List<EndpointConfig> Endpoints { get; init; } = new();
            }

            [ConfigSection("__endpoint__")]
            public partial record EndpointConfig
            {
                public string Url { get; init; } = default!;
            }
            """;

        var result = GeneratorHarness.Run(Source);
        var generated = result.GetEmittedConfigSource();

        // List<T>.Count is the size member for non-array collections.
        Assert.Contains("options.Endpoints.Count < 1", generated);
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "CB0007");
    }

    [Fact]
    public void MaxLength_on_nested_config_array_emits_length_check()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Api")]
            public partial record ApiConfig
            {
                [MaxLength(3)]
                public EndpointConfig[] Endpoints { get; init; } = System.Array.Empty<EndpointConfig>();
            }

            [ConfigSection("__endpoint__")]
            public partial record EndpointConfig
            {
                public string Url { get; init; } = default!;
            }
            """;

        var result = GeneratorHarness.Run(Source);
        var generated = result.GetEmittedConfigSource();

        // T[] uses .Length.
        Assert.Contains("options.Endpoints.Length > 3", generated);
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "CB0007");
    }

    [Fact]
    public void MinLength_on_nested_config_dictionary_emits_count_check()
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
                public Dictionary<string, TenantConfig> Tenants { get; init; } = new();
            }

            [ConfigSection("__tenant__")]
            public partial record TenantConfig
            {
                public string BaseUrl { get; init; } = default!;
            }
            """;

        var result = GeneratorHarness.Run(Source);
        var generated = result.GetEmittedConfigSource();

        // Dictionary exposes .Count, not .Length — widened SizeProperty must
        // pick Count for NestedConfigDictionary.
        Assert.Contains("options.Tenants.Count < 1", generated);
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "CB0007");
    }

    [Fact]
    public void MaxLength_on_nested_config_dictionary_emits_count_check()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.Collections.Generic;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Api")]
            public partial record ApiConfig
            {
                [MaxLength(3)]
                public Dictionary<string, TenantConfig> Tenants { get; init; } = new();
            }

            [ConfigSection("__tenant__")]
            public partial record TenantConfig
            {
                public string BaseUrl { get; init; } = default!;
            }
            """;

        var result = GeneratorHarness.Run(Source);
        var generated = result.GetEmittedConfigSource();

        Assert.Contains("options.Tenants.Count > 3", generated);
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "CB0007");
    }

    // ── Diagnostic tests ─────────────────────────────────────────────────

    [Fact]
    public void Range_on_string_produces_CB0006()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Test")]
            public partial record TestConfig
            {
                [Range(1, 100)]
                public string Name { get; init; } = default!;
            }
            """;

        var result = GeneratorHarness.Run(Source);

        Assert.Contains(result.Diagnostics, d => d.Id == "CB0006");
    }

    [Fact]
    public void StringLength_on_int_produces_CB0007()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Test")]
            public partial record TestConfig
            {
                [StringLength(100)]
                public int Port { get; init; }
            }
            """;

        var result = GeneratorHarness.Run(Source);

        Assert.Contains(result.Diagnostics, d => d.Id == "CB0007");
    }

    [Fact]
    public void Invalid_regex_produces_CB0008()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Test")]
            public partial record TestConfig
            {
                [RegularExpression("[invalid")]
                public string Name { get; init; } = default!;
            }
            """;

        var result = GeneratorHarness.Run(Source);

        Assert.Contains(result.Diagnostics, d => d.Id == "CB0008");
    }

    [Fact]
    public void Required_on_non_nullable_produces_CB0009()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Test")]
            public partial record TestConfig
            {
                [Required]
                public string Name { get; init; } = default!;
            }
            """;

        var result = GeneratorHarness.Run(Source);

        Assert.Contains(result.Diagnostics, d => d.Id == "CB0009");
    }

    // ── End-to-end validation tests ──────────────────────────────────────

    [Fact]
    public void Range_validation_fails_for_out_of_bounds_value()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Test")]
            public partial record TestConfig
            {
                [Range(1, 65535)]
                public int Port { get; init; } = 5432;
            }
            """;

        // CompileAndBind resolves IOptions<T>.Value which runs validation.
        // A bad value will throw OptionsValidationException — that IS the
        // validation working. We catch it and assert on the message.
        var ex = Assert.ThrowsAny<System.Exception>(() =>
            GeneratorHarness.CompileAndBind(
                Source,
                "TestConfig",
                new System.Collections.Generic.Dictionary<string, string?> { ["Test:Port"] = "0" }));

        Assert.Contains("must be between 1 and 65535", ex.InnerException?.Message ?? ex.Message);
    }

    [Fact]
    public void Range_validation_passes_for_in_bounds_value()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Test")]
            public partial record TestConfig
            {
                [Range(1, 65535)]
                public int Port { get; init; } = 5432;
            }
            """;

        var bound = GeneratorHarness.CompileAndBind(
            Source,
            "TestConfig",
            new System.Collections.Generic.Dictionary<string, string?> { ["Test:Port"] = "8080" });

        var validatorType = bound.GetType().GetNestedType("Validator")!;
        var validator = System.Activator.CreateInstance(validatorType)!;
        // The generator emits two Validate overloads (the IValidateOptions<T>
        // two-arg entry point and a path-aware three-arg one). Disambiguate by
        // explicit parameter types.
        var validateMethod = validatorType.GetMethod(
            "Validate",
            new[] { typeof(string), bound.GetType() })!;
        var result = validateMethod.Invoke(validator, new object?[] { null, bound })!;
        var succeeded = (bool)result.GetType().GetProperty("Succeeded")!.GetValue(result)!;

        Assert.True(succeeded, "Validation should pass for Port = 8080 with [Range(1, 65535)]");
    }

    [Fact]
    public void Regex_validation_fails_for_non_matching_string()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Test")]
            public partial record TestConfig
            {
                [RegularExpression(@"^https?://")]
                public string Endpoint { get; init; } = default!;
            }
            """;

        var ex = Assert.ThrowsAny<System.Exception>(() =>
            GeneratorHarness.CompileAndBind(
                Source,
                "TestConfig",
                new System.Collections.Generic.Dictionary<string, string?> { ["Test:Endpoint"] = "ftp://example.com" }));

        Assert.Contains("does not match the required pattern", ex.InnerException?.Message ?? ex.Message);
    }

    // ── ErrorMessage support ─────────────────────────────────────────────

    [Fact]
    public void Range_ErrorMessage_overrides_default_and_substitutes_placeholders()
    {
        // {0} = "[SectionName:PropName]", {1} = min, {2} = max.
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Test")]
            public partial record TestConfig
            {
                [Range(1, 65535, ErrorMessage = "Port {0} must be between {1} and {2}.")]
                public int Port { get; init; } = 5432;
            }
            """;

        var ex = Assert.ThrowsAny<System.Exception>(() =>
            GeneratorHarness.CompileAndBind(
                Source,
                "TestConfig",
                new System.Collections.Generic.Dictionary<string, string?> { ["Test:Port"] = "0" }));

        var message = ex.InnerException?.Message ?? ex.Message;

        // Custom message is present; default "must be between" is NOT (they'd coexist if defaults weren't suppressed).
        Assert.Contains("Port [Test:Port] must be between 1 and 65535.", message);
    }

    [Fact]
    public void RegularExpression_ErrorMessage_without_placeholders_is_used_verbatim()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Test")]
            public partial record TestConfig
            {
                [RegularExpression(@"^https?://", ErrorMessage = "Must be an http(s) URL.")]
                public string Endpoint { get; init; } = default!;
            }
            """;

        var ex = Assert.ThrowsAny<System.Exception>(() =>
            GeneratorHarness.CompileAndBind(
                Source,
                "TestConfig",
                new System.Collections.Generic.Dictionary<string, string?> { ["Test:Endpoint"] = "ftp://example.com" }));

        var message = ex.InnerException?.Message ?? ex.Message;
        Assert.Contains("Must be an http(s) URL.", message);
        // Default message should NOT appear since ErrorMessage overrides it.
        Assert.DoesNotContain("does not match the required pattern", message);
    }

    [Fact]
    public void MinLength_ErrorMessage_supports_arg1_placeholder()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Test")]
            public partial record TestConfig
            {
                [MinLength(8, ErrorMessage = "{0}: at least {1} chars required.")]
                public string Password { get; init; } = default!;
            }
            """;

        var ex = Assert.ThrowsAny<System.Exception>(() =>
            GeneratorHarness.CompileAndBind(
                Source,
                "TestConfig",
                new System.Collections.Generic.Dictionary<string, string?> { ["Test:Password"] = "short" }));

        var message = ex.InnerException?.Message ?? ex.Message;
        Assert.Contains("[Test:Password]: at least 8 chars required.", message);
    }

    [Fact]
    public void Range_without_ErrorMessage_uses_default_message()
    {
        // Regression: confirm the default-message path still works.
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Test")]
            public partial record TestConfig
            {
                [Range(1, 10)]
                public int Count { get; init; }
            }
            """;

        var ex = Assert.ThrowsAny<System.Exception>(() =>
            GeneratorHarness.CompileAndBind(
                Source,
                "TestConfig",
                new System.Collections.Generic.Dictionary<string, string?> { ["Test:Count"] = "99" }));

        Assert.Contains("must be between 1 and 10", ex.InnerException?.Message ?? ex.Message);
    }
}
