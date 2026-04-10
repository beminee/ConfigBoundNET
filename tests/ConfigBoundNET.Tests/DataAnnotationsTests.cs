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
        var validateMethod = validatorType.GetMethod("Validate")!;
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
}
