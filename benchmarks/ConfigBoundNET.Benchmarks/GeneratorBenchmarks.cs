// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Basic.Reference.Assemblies;
using BenchmarkDotNet.Attributes;
using ConfigBoundNET;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ConfigBoundNET.Benchmarks;

/// <summary>
/// Measures the incremental source generator itself — how fast ConfigBoundNET
/// runs during compilation. These numbers answer the question every consumer
/// hits first: "what does this generator cost my build?"
/// </summary>
/// <remarks>
/// <para>Two scenarios per type-count parameter:</para>
/// <list type="bullet">
///   <item><description>
///     <c>ColdRun</c> — fresh driver + full compilation. Represents the
///     first-build cost: Roslyn has no cached incremental outputs, so the
///     generator re-runs every transform step from scratch for every input
///     type.
///   </description></item>
///   <item><description>
///     <c>Incremental_UnrelatedEdit</c> — warmed driver + unrelated syntax
///     tree added. Represents the steady-state IDE/edit cost: the user
///     modifies code that has nothing to do with
///     <c>[ConfigSection]</c> types. Every output should hit the Roslyn
///     cache (<see cref="IncrementalStepRunReason.Cached"/> or
///     <see cref="IncrementalStepRunReason.Unchanged"/>), so this run should
///     be near-free — any meaningful fraction of <c>ColdRun</c> cost is a
///     caching regression worth investigating.
///   </description></item>
/// </list>
/// <para>
/// The <see cref="TypeCount"/> parameter surfaces scaling behaviour. A
/// healthy generator should scale near-linearly in cold-run cost and stay
/// essentially flat in the incremental path regardless of type count.
/// </para>
/// <para>
/// Setup cost (synthetic source generation + <c>CSharpCompilation</c> build +
/// warmed driver seeding) is paid once per benchmark instance via
/// <c>GlobalSetup</c>, so the hot loop measures only the generator step,
/// not Roslyn compilation overhead.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class GeneratorBenchmarks
{
    /// <summary>
    /// Number of <c>[ConfigSection]</c>-annotated types in the synthetic
    /// compilation. Three data points surface scaling: a trivial project,
    /// a realistic medium project, and a large one.
    /// </summary>
    [Params(1, 10, 50)]
    public int TypeCount { get; set; }

    private CSharpCompilation _compilation = default!;
    private CSharpParseOptions _parseOptions = default!;
    private SyntaxTree _unrelatedTree = default!;
    private GeneratorDriver _warmedDriver = default!;

    [GlobalSetup]
    public void Setup()
    {
        _parseOptions = new CSharpParseOptions(LanguageVersion.Latest);

        var source = GenerateSyntheticTypes(TypeCount);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, _parseOptions);

        _compilation = CSharpCompilation.Create(
            assemblyName: "ConfigBoundNET.Benchmarks.GeneratorInput",
            syntaxTrees: new[] { syntaxTree },
            references: BuildReferences(),
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        _unrelatedTree = CSharpSyntaxTree.ParseText(
            """
            namespace UnrelatedBench;
            public sealed class NoiseHelper
            {
                public int Compute(int x) => x * 2;
            }
            """,
            _parseOptions);

        // Warmed driver: one full run against the baseline compilation, then
        // hand it to every incremental benchmark iteration. Each iteration
        // calls RunGenerators on the warmed driver (which doesn't mutate it)
        // so every call re-exercises the cache from the same starting point.
        _warmedDriver = CreateDriver().RunGenerators(_compilation);
    }

    [Benchmark(Baseline = true, Description = "Cold run: fresh driver")]
    public GeneratorDriver ColdRun()
    {
        var driver = CreateDriver();
        return driver.RunGenerators(_compilation);
    }

    [Benchmark(Description = "Incremental: warmed driver + unrelated edit")]
    public GeneratorDriver Incremental_UnrelatedEdit()
    {
        var extended = _compilation.AddSyntaxTrees(_unrelatedTree);
        return _warmedDriver.RunGenerators(extended);
    }

    /// <summary>
    /// Produces a <c>CSharpGeneratorDriver</c> configured the same way the
    /// real build pipeline configures it. The generator driver under test is
    /// <see cref="ConfigBoundGenerator"/>.
    /// </summary>
    private CSharpGeneratorDriver CreateDriver() => CSharpGeneratorDriver.Create(
        generators: new[] { new ConfigBoundGenerator().AsSourceGenerator() },
        additionalTexts: ImmutableArray<AdditionalText>.Empty,
        parseOptions: _parseOptions,
        optionsProvider: null);

    /// <summary>
    /// Reference set for the synthetic compilation. Mirrors the test
    /// harness's <c>References</c> field so the generator sees the same BCL
    /// surface it sees in real use.
    /// </summary>
    private static ImmutableArray<MetadataReference> BuildReferences() =>
        Net100.References.All
            .Cast<MetadataReference>()
            .Concat(new MetadataReference[]
            {
                MetadataReference.CreateFromFile(typeof(IConfiguration).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(IServiceCollection).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(ServiceCollectionDescriptorExtensions).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(IOptions<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(OptionsConfigurationServiceCollectionExtensions).Assembly.Location),
            })
            .ToImmutableArray();

    /// <summary>
    /// Builds a source file containing <paramref name="count"/> distinct
    /// <c>[ConfigSection]</c>-annotated records. Each record is representative
    /// of a real-world config shape: a required string, a
    /// <c>[Range]</c>-constrained int, and an optional <see cref="TimeSpan"/>.
    /// Types are deliberately independent (no nested configs, no cross-
    /// references) so the generator workload scales purely with the type count.
    /// </summary>
    private static string GenerateSyntheticTypes(int count)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using ConfigBoundNET;");
        sb.AppendLine("using System.ComponentModel.DataAnnotations;");
        sb.AppendLine();
        sb.AppendLine("namespace SyntheticBench;");
        sb.AppendLine();

        for (int i = 0; i < count; i++)
        {
            var index = i.ToString(CultureInfo.InvariantCulture);
            sb.Append("[ConfigSection(\"Section").Append(index).AppendLine("\")]");
            sb.Append("public partial record Config").AppendLine(index);
            sb.AppendLine("{");
            sb.AppendLine("    public string ApiKey { get; init; } = default!;");
            sb.AppendLine("    [Range(1, 100)]");
            sb.AppendLine("    public int MaxRetries { get; init; }");
            sb.AppendLine("    public System.TimeSpan Timeout { get; init; }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
