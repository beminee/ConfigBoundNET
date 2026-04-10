// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConfigBoundNET.Tests;

/// <summary>
/// Behavioural tests for <see cref="ConfigBoundGenerator"/>.
/// </summary>
/// <remarks>
/// These tests run the generator against hand-rolled C# snippets via
/// <see cref="GeneratorHarness"/> and assert on both emitted source and
/// diagnostics. They intentionally stop short of full snapshot testing —
/// substring assertions are more resilient to formatting tweaks and give
/// clearer failure messages when they do break.
/// </remarks>
public sealed class ConfigBoundGeneratorTests
{
    [Fact]
    public void Attribute_is_emitted_even_when_no_types_are_annotated()
    {
        // The post-init hook should produce the attribute regardless of whether
        // any user code references it yet. This keeps the "first use" experience
        // smooth — IntelliSense finds the attribute as soon as the package is
        // installed.
        var result = GeneratorHarness.Run("namespace Empty;");

        Assert.Contains(
            result.GeneratedTrees,
            tree => tree.FilePath.EndsWith(AttributeSource.HintName, System.StringComparison.Ordinal));
    }

    [Fact]
    public void Valid_record_produces_validator_and_extension_method()
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

        var result = GeneratorHarness.Run(Source);

        // No diagnostics expected at all — the type is well-formed.
        Assert.Empty(result.Diagnostics);

        var generated = result.GetEmittedConfigSource();

        // The generated file should contain:
        // 1. A constant exposing the section name
        Assert.Contains("public const string SectionName = \"Db\";", generated);
        // 2. A nested Validator implementing IValidateOptions<DbConfig>
        Assert.Contains("IValidateOptions<DbConfig>", generated);
        Assert.Contains("public sealed class Validator", generated);
        // 3. A null/empty check for the required Conn property
        Assert.Contains("string.IsNullOrWhiteSpace(options.Conn)", generated);
        // 4. A DI extension method named AddDbConfig
        Assert.Contains("public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddDbConfig", generated);
        // 5. Idempotent validator registration
        Assert.Contains("TryAddEnumerable", generated);
    }

    [Fact]
    public void Nullable_reference_properties_are_not_required()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Db")]
            public partial record DbConfig
            {
                public string? OptionalName { get; init; }
            }
            """;

        var result = GeneratorHarness.Run(Source);
        var generated = result.GetEmittedConfigSource();

        // The optional property must still be *bound* (so users get the value
        // when configuration provides one), but it must not appear in the
        // generated validator's IsNullOrWhiteSpace / null-check pass — only
        // non-nullable reference types are validated.
        Assert.DoesNotContain("IsNullOrWhiteSpace(options.OptionalName)", generated);
        Assert.DoesNotContain("options.OptionalName is null", generated);
    }

    [Fact]
    public void Non_partial_type_produces_CB0001()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Db")]
            public record DbConfig(string Conn);
            """;

        var result = GeneratorHarness.Run(Source);

        Assert.Contains(result.Diagnostics, d => d.Id == "CB0001");

        // When the type fails validation we should *not* emit any per-type
        // output (only the post-init attribute + OptionsFactory helper),
        // otherwise the user would see a second compile error masking the real one.
        Assert.Empty(result.NonPostInitGeneratedTrees());
    }

    [Fact]
    public void Empty_section_name_produces_CB0002()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("  ")]
            public partial record DbConfig
            {
                public string Conn { get; init; } = default!;
            }
            """;

        var result = GeneratorHarness.Run(Source);

        Assert.Contains(result.Diagnostics, d => d.Id == "CB0002");
    }

    [Fact]
    public void Nested_type_produces_CB0003()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            public static class Outer
            {
                [ConfigSection("Db")]
                public partial record DbConfig
                {
                    public string Conn { get; init; } = default!;
                }
            }
            """;

        var result = GeneratorHarness.Run(Source);

        Assert.Contains(result.Diagnostics, d => d.Id == "CB0003");
    }

    [Fact]
    public void Empty_bindable_surface_produces_CB0004_warning_but_still_emits()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Db")]
            public partial record DbConfig;
            """;

        var result = GeneratorHarness.Run(Source);

        Assert.Contains(result.Diagnostics, d => d.Id == "CB0004" && d.Severity == DiagnosticSeverity.Warning);

        // CB0004 is advisory — the generator should still emit the file so that
        // the user can call AddDbConfig() even on an empty type.
        var generated = result.GetEmittedConfigSource();
        Assert.Contains("public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddDbConfig", generated);
    }

    [Fact]
    public void Generated_source_for_two_types_uses_distinct_hint_names()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("A")]
            public partial record AConfig { public string X { get; init; } = default!; }

            [ConfigSection("B")]
            public partial record BConfig { public string Y { get; init; } = default!; }
            """;

        var result = GeneratorHarness.Run(Source);
        var fileNames = result.GeneratedTrees
            .Select(t => System.IO.Path.GetFileName(t.FilePath))
            .ToArray();

        // Sanity-check uniqueness so we never regress into overwriting source.
        Assert.Equal(fileNames.Length, fileNames.Distinct().Count());
    }
}
