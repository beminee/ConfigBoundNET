// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Basic.Reference.Assemblies;
using ConfigBoundNET;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ConfigBoundNET.Tests;

/// <summary>
/// Small helper that builds a <see cref="CSharpCompilation"/> plus a
/// <see cref="CSharpGeneratorDriver"/> around the <see cref="ConfigBoundGenerator"/>
/// so individual tests stay terse.
/// </summary>
internal static class GeneratorHarness
{
    /// <summary>
    /// The compile-time references fed into every synthetic compilation.
    /// </summary>
    /// <remarks>
    /// <see cref="Net80"/> supplies a canonical .NET 8 reference-assembly set,
    /// which is the most reliable way to give Roslyn a realistic BCL surface
    /// without depending on the test host's own runtime layout. On top of that
    /// we add the specific Microsoft.Extensions.* assemblies required by the
    /// generated code.
    /// </remarks>
    private static readonly ImmutableArray<MetadataReference> References =
        Net80.References.All
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
    /// Compiles <paramref name="source"/>, runs the generator over it, and
    /// returns the driver's result for inspection.
    /// </summary>
    /// <param name="source">The C# source under test. Usually contains a single record annotated with <c>[ConfigSection]</c>.</param>
    public static GeneratorDriverRunResult Run(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);

        var compilation = CSharpCompilation.Create(
            assemblyName: "ConfigBoundNET.Tests.Dynamic",
            syntaxTrees: new[] { syntaxTree },
            references: References,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver.Create(
            generators: new[] { new ConfigBoundGenerator().AsSourceGenerator() },
            additionalTexts: ImmutableArray<AdditionalText>.Empty,
            parseOptions: parseOptions,
            optionsProvider: null);

        return driver.RunGenerators(compilation).GetRunResult();
    }

    /// <summary>
    /// Convenience: returns the generated source text for the single non-attribute
    /// source tree produced by the generator run (i.e. the emitted validator file).
    /// </summary>
    /// <remarks>Throws if no such file exists — tests that expect zero output should use <see cref="Run"/>.</remarks>
    public static string GetEmittedConfigSource(this GeneratorDriverRunResult result)
    {
        // Skip the attribute file; we only care about the per-type output here.
        var emitted = result.GeneratedTrees
            .Where(tree => !tree.FilePath.EndsWith(AttributeSource.HintName, System.StringComparison.Ordinal))
            .ToArray();

        if (emitted.Length == 0)
        {
            throw new System.InvalidOperationException("Generator produced no non-attribute output files.");
        }

        return emitted[0].ToString();
    }
}
