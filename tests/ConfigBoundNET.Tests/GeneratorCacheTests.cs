// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System.Linq;
using ConfigBoundNET;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConfigBoundNET.Tests;

/// <summary>
/// Incremental-caching tests for <see cref="ConfigBoundGenerator"/>.
/// </summary>
/// <remarks>
/// <para>
/// Roslyn's incremental pipeline caches each step's output by value-equality.
/// If an accidental non-equatable type (a raw <c>T[]</c>, an <c>ISymbol</c>,
/// a <c>List&lt;T&gt;</c>) slips into a pipeline model, the cache silently
/// starts missing on every edit — the user sees IDE slowdown with no build
/// error to pinpoint the regression.
/// </para>
/// <para>
/// These tests pin the invariant that unrelated compilation edits do not
/// invalidate our transform step's cache. A future PR that breaks
/// incremental caching will fail these tests loudly at unit-test time.
/// </para>
/// </remarks>
public sealed class GeneratorCacheTests
{
    /// <summary>
    /// A deliberately feature-rich baseline so cache tests exercise every
    /// model-equality code path that matters: DataAnnotations bundled into
    /// an <see cref="EquatableArray{T}"/>, collections (array / list /
    /// dictionary), a nested <c>[ConfigSection]</c> type (recursive
    /// constructor + recursive validation), and multiple scalar types.
    /// If a future refactor breaks equality on any of these surfaces, the
    /// unrelated-edit test flips from <c>Unchanged</c> to <c>Modified</c>
    /// and the suite fails loudly.
    /// </summary>
    private const string BaselineSource = """
        using ConfigBoundNET;
        using System;
        using System.Collections.Generic;
        using System.ComponentModel.DataAnnotations;

        namespace MyApp;

        [ConfigSection("Db")]
        public partial record DbConfig
        {
            [Sensitive]
            public string Conn { get; init; } = default!;

            [Range(1, 65535)]
            public int Port { get; init; } = 5432;

            [StringLength(100, MinimumLength = 3)]
            public string DisplayName { get; init; } = default!;

            [RegularExpression(@"^[a-z]+$", ErrorMessage = "Lowercase only.")]
            public string Tag { get; init; } = default!;

            public LogLevel Level { get; init; }
            public Guid TenantId { get; init; }
            public TimeSpan Timeout { get; init; }

            public string[] Hosts { get; init; } = Array.Empty<string>();
            public List<int> Ports { get; init; } = new();
            public Dictionary<string, string> Headers { get; init; } = new();

            public RetryConfig Retry { get; init; } = default!;

            public List<EndpointConfig> Endpoints { get; init; } = new();

            public Dictionary<string, EndpointConfig> Tenants { get; init; } = new();
        }

        [ConfigSection("Retry")]
        public partial record RetryConfig
        {
            [Range(1, 20)]
            public int MaxAttempts { get; init; } = 3;
        }

        [ConfigSection("__endpoint__")]
        public partial record EndpointConfig
        {
            public string Url { get; init; } = default!;
        }

        public enum LogLevel { Trace, Debug, Info, Warn, Error }
        """;

    [Fact]
    public void Unrelated_compilation_edit_does_not_invalidate_cache()
    {
        // Arrange: run the generator once to seed the cache.
        var (driver, compilation) = GeneratorHarness.CreateDriverWithTracking(BaselineSource);

        // Act: add an unrelated syntax tree that contains NO [ConfigSection]
        // types. Adding it is enough to make ForAttributeWithMetadataName
        // re-evaluate (the syntax tree set is part of its input), but the
        // transform should still produce the SAME BuildResult for DbConfig.
        var unrelatedTree = CSharpSyntaxTree.ParseText("""
            namespace Unrelated;

            public sealed class Helper
            {
                public int Compute(int x) => x * 2;
            }
            """);

        compilation = compilation.AddSyntaxTrees(unrelatedTree);
        driver = driver.RunGenerators(compilation);

        // Assert: every output of the BuildResults transform must report
        // either Cached (step didn't re-run) or Unchanged (step re-ran but
        // produced an equal output). Both prove the value-equality machinery
        // is working. Modified or New here would indicate a non-equatable
        // type has leaked into the pipeline model — the whole reason this
        // test exists.
        AssertAllStepOutputsAreCacheable(driver, TrackingNames.BuildResults);
    }

    [Fact]
    public void Adding_another_config_type_produces_new_cache_entry()
    {
        // Sanity check that the cache discriminates: adding a new [ConfigSection]
        // type should produce a New output for that type while leaving the
        // existing type's output Cached. This proves the tracking mechanism
        // actually observes per-input cache state.
        var (driver, compilation) = GeneratorHarness.CreateDriverWithTracking(BaselineSource);

        var newConfigTree = CSharpSyntaxTree.ParseText("""
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Cache")]
            public partial record CacheConfig
            {
                public string Host { get; init; } = default!;
            }
            """);

        compilation = compilation.AddSyntaxTrees(newConfigTree);
        driver = driver.RunGenerators(compilation);

        var result = driver.GetRunResult().Results[0];
        var outputs = result.TrackedSteps[TrackingNames.BuildResults]
            .SelectMany(step => step.Outputs)
            .Select(o => o.Reason)
            .ToList();

        // We should see a "cache-friendly" output for the pre-existing DbConfig
        // (either Cached or Unchanged — see Unrelated_compilation_edit_does_not_invalidate_cache
        // for why Unchanged is a normal outcome here) and a New output for
        // the freshly added CacheConfig.
        Assert.Contains(outputs, r => r is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged);
        Assert.Contains(IncrementalStepRunReason.New, outputs);
    }

    [Fact]
    public void Changing_the_config_type_invalidates_cache()
    {
        // Sanity check in the opposite direction: when the [ConfigSection] type
        // itself changes, the cache MUST be invalidated. Otherwise "everything
        // is cached" would be a trivial pass and wouldn't prove anything.
        var (driver, compilation) = GeneratorHarness.CreateDriverWithTracking(BaselineSource);

        // Replace DbConfig's syntax tree with a modified version that has an
        // extra property. This is a real change to our generator's inputs.
        var original = compilation.SyntaxTrees.First();
        var modifiedSource = original.GetText().ToString().Replace(
            "public int Port { get; init; } = 5432;",
            "public int Port { get; init; } = 5432;\n    public bool Enabled { get; init; }");

        var modifiedTree = CSharpSyntaxTree.ParseText(modifiedSource);
        compilation = compilation.ReplaceSyntaxTree(original, modifiedTree);
        driver = driver.RunGenerators(compilation);

        var result = driver.GetRunResult().Results[0];
        var outputs = result.TrackedSteps[TrackingNames.BuildResults]
            .SelectMany(step => step.Outputs)
            .Select(o => o.Reason)
            .ToList();

        // The transform ran because DbConfig's input changed. At least one
        // output reason must reflect that real difference — Modified (same
        // key, different value) or New (re-identified). The other baseline
        // type (RetryConfig) is legitimately Unchanged since it wasn't
        // touched, so we only require that the change was detected
        // somewhere, not that every output is non-cache-friendly.
        Assert.Contains(outputs, r => r is IncrementalStepRunReason.Modified or IncrementalStepRunReason.New);
    }

    [Fact]
    public void Unrelated_edit_does_not_reemit_source_output()
    {
        // BuildResults caching is necessary but not sufficient: even if the
        // transform reports Cached, a non-equatable type further downstream
        // could still force RegisterSourceOutput to re-run and re-emit the
        // generated file — which is the IDE slowdown this whole test file
        // exists to prevent. GeneratorRunResult.TrackedOutputSteps records
        // what happened at the actual source-output boundary, so asserting
        // on it pins the end-to-end cache behaviour, not just the midpoint.
        var (driver, compilation) = GeneratorHarness.CreateDriverWithTracking(BaselineSource);

        var unrelatedTree = CSharpSyntaxTree.ParseText("""
            namespace Unrelated;

            public sealed class Helper
            {
                public int Compute(int x) => x * 2;
            }
            """);

        compilation = compilation.AddSyntaxTrees(unrelatedTree);
        driver = driver.RunGenerators(compilation);

        var result = driver.GetRunResult().Results[0];
        Assert.NotEmpty(result.TrackedOutputSteps);

        foreach (var (outputName, steps) in result.TrackedOutputSteps)
        {
            foreach (var step in steps)
            {
                foreach (var output in step.Outputs)
                {
                    var cacheFriendly = output.Reason
                        is IncrementalStepRunReason.Cached
                        or IncrementalStepRunReason.Unchanged;

                    Assert.True(
                        cacheFriendly,
                        $"Output step '{outputName}' reason was {output.Reason}; expected Cached or Unchanged. " +
                        "An unrelated compilation edit re-emitted source output, which means the pipeline " +
                        "is not actually cache-stable end-to-end even if the BuildResults transform reports Cached.");
                }
            }
        }
    }

    [Fact]
    public void Adding_a_nested_reference_flips_target_aggregate_entry()
    {
        // AggregateEntry now carries IsReferencedAsNested, a cross-type fact
        // derived from scanning every model's property list for nested-config
        // references. Introducing a new List<T> property that references
        // another [ConfigSection] type MUST invalidate the aggregate cache
        // for the target type (its classification just flipped from
        // top-level to nested-referenced), but must NOT bleed into
        // aggregate cache for unrelated types.
        const string Baseline = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Db")]
            public partial record DbConfig
            {
                public string Conn { get; init; } = default!;
            }

            [ConfigSection("Endpoint")]
            public partial record EndpointConfig
            {
                public string Url { get; init; } = default!;
            }
            """;

        var (driver, compilation) = GeneratorHarness.CreateDriverWithTracking(Baseline);

        // Introduce a nested reference to EndpointConfig from DbConfig.
        // Inference should now classify EndpointConfig as
        // IsReferencedAsNested=true, producing a Modified output on the
        // AggregateEntries tracking step.
        var original = compilation.SyntaxTrees.First();
        var modifiedSource = original.GetText().ToString().Replace(
            "public string Conn { get; init; } = default!;",
            "public string Conn { get; init; } = default!;\n    public System.Collections.Generic.List<EndpointConfig> Endpoints { get; init; } = new();");

        var modifiedTree = CSharpSyntaxTree.ParseText(modifiedSource);
        compilation = compilation.ReplaceSyntaxTree(original, modifiedTree);
        driver = driver.RunGenerators(compilation);

        var result = driver.GetRunResult().Results[0];
        var outputs = result.TrackedSteps[TrackingNames.AggregateEntries]
            .SelectMany(step => step.Outputs)
            .Select(o => o.Reason)
            .ToList();

        // Adding the nested reference flips EndpointConfig's flag, so the
        // aggregate-entries array is no longer byte-equal to the baseline.
        // Expect at least one Modified reason.
        Assert.Contains(outputs, r => r is IncrementalStepRunReason.Modified or IncrementalStepRunReason.New);
    }

    [Fact]
    public void Toggling_IsNestedOnly_flips_aggregate_entry()
    {
        // Parallel to the inference test: setting IsNestedOnly=true on
        // the attribute must flip the target's IsReferencedAsNested flag
        // even though no in-compilation nested reference exists. This is
        // the cross-assembly library-author path.
        const string Baseline = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("__ep__")]
            public partial record EndpointConfig
            {
                public string Url { get; init; } = default!;
            }
            """;

        var (driver, compilation) = GeneratorHarness.CreateDriverWithTracking(Baseline);

        var original = compilation.SyntaxTrees.First();
        var modifiedSource = original.GetText().ToString().Replace(
            "[ConfigSection(\"__ep__\")]",
            "[ConfigSection(\"__ep__\", IsNestedOnly = true)]");

        var modifiedTree = CSharpSyntaxTree.ParseText(modifiedSource);
        compilation = compilation.ReplaceSyntaxTree(original, modifiedTree);
        driver = driver.RunGenerators(compilation);

        var result = driver.GetRunResult().Results[0];
        var outputs = result.TrackedSteps[TrackingNames.AggregateEntries]
            .SelectMany(step => step.Outputs)
            .Select(o => o.Reason)
            .ToList();

        Assert.Contains(outputs, r => r is IncrementalStepRunReason.Modified or IncrementalStepRunReason.New);
    }

    [Fact]
    public void Editing_unrelated_annotation_does_not_invalidate_aggregate()
    {
        // The AggregateEntries pipeline step carries only (Namespace, TypeName)
        // per type — not the full property / annotation detail. Changing a
        // [Range] on one property must therefore NOT invalidate the aggregate
        // step's cache, even though it DOES invalidate the per-type
        // BuildResults step. This test pins that isolation so a future leak
        // of per-type detail into AggregateEntry fails loudly.
        var (driver, compilation) = GeneratorHarness.CreateDriverWithTracking(BaselineSource);

        // Edit a [Range(1, 20)] bound to [Range(1, 30)] on RetryConfig.MaxAttempts.
        // Semantically meaningful for per-type validator emission, but leaves
        // the set of [ConfigSection] type identities unchanged.
        var original = compilation.SyntaxTrees.First();
        var modifiedSource = original.GetText().ToString().Replace(
            "[Range(1, 20)]",
            "[Range(1, 30)]");

        var modifiedTree = CSharpSyntaxTree.ParseText(modifiedSource);
        compilation = compilation.ReplaceSyntaxTree(original, modifiedTree);
        driver = driver.RunGenerators(compilation);

        AssertAllStepOutputsAreCacheable(driver, TrackingNames.AggregateEntries);
    }

    [Fact]
    public void Whitespace_edit_in_unrelated_tree_does_not_invalidate_cache()
    {
        // A stricter variant of Unrelated_compilation_edit_does_not_invalidate_cache:
        // instead of adding a brand new tree, we modify an existing unrelated tree
        // in place (the kind of edit a user makes constantly while typing in another
        // file). ReplaceSyntaxTree is closer to the "per-keystroke" IDE experience
        // than AddSyntaxTrees and catches regressions where the pipeline is sensitive
        // to syntax-tree identity rather than content.
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var unrelatedTree = CSharpSyntaxTree.ParseText("""
            namespace Unrelated;

            public sealed class Helper
            {
                public int Compute(int x) => x * 2;
            }
            """, parseOptions);

        var (driver, compilation) = GeneratorHarness.CreateDriverWithTracking(BaselineSource);
        compilation = compilation.AddSyntaxTrees(unrelatedTree);
        driver = driver.RunGenerators(compilation);

        // Simulate "user edits a comment inside Helper.cs". Same syntax-tree slot,
        // different content. This is the most common IDE edit shape and the one
        // most likely to expose a tree-identity-based cache bug.
        var editedUnrelatedTree = CSharpSyntaxTree.ParseText("""
            namespace Unrelated;

            // user added a note here
            public sealed class Helper
            {
                public int Compute(int x) => x * 2;
            }
            """, parseOptions);

        compilation = compilation.ReplaceSyntaxTree(unrelatedTree, editedUnrelatedTree);
        driver = driver.RunGenerators(compilation);

        AssertAllStepOutputsAreCacheable(driver, TrackingNames.BuildResults);
    }

    /// <summary>
    /// Asserts that every output of the named pipeline step reports a
    /// "cache-friendly" outcome — either <see cref="IncrementalStepRunReason.Cached"/>
    /// (step did not re-run) or <see cref="IncrementalStepRunReason.Unchanged"/>
    /// (step re-ran but produced an equal output). Both prove that the value
    /// equality machinery is working. <see cref="IncrementalStepRunReason.Modified"/>
    /// or <see cref="IncrementalStepRunReason.New"/> here would indicate a
    /// non-equatable type has leaked into the pipeline model.
    /// </summary>
    private static void AssertAllStepOutputsAreCacheable(GeneratorDriver driver, string trackingName)
    {
        var result = driver.GetRunResult().Results[0];
        Assert.True(
            result.TrackedSteps.ContainsKey(trackingName),
            $"Pipeline did not produce any step with tracking name '{trackingName}'. " +
            $"Known steps: {string.Join(", ", result.TrackedSteps.Keys)}");

        var steps = result.TrackedSteps[trackingName];
        foreach (var step in steps)
        {
            foreach (var output in step.Outputs)
            {
                var cacheFriendly = output.Reason
                    is IncrementalStepRunReason.Cached
                    or IncrementalStepRunReason.Unchanged;

                Assert.True(
                    cacheFriendly,
                    $"Step '{trackingName}' output reason was {output.Reason}; expected Cached or Unchanged. " +
                    "A Modified or New outcome on an unrelated compilation edit indicates a non-equatable " +
                    "type has leaked into the pipeline model — check that every model field is either a " +
                    "primitive, a string, or an EquatableArray<T> of an IEquatable<T> element.");
            }
        }
    }
}
