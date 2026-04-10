// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
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
    /// <see cref="Net100"/> supplies a canonical .NET 10 reference-assembly set,
    /// which is the most reliable way to give Roslyn a realistic BCL surface
    /// without depending on the test host's own runtime layout. The version
    /// must match the System.Runtime version that the
    /// <c>Microsoft.Extensions.*</c> runtime packages added below were built
    /// against, otherwise the generator output fails to compile with CS1705.
    /// </remarks>
    private static readonly ImmutableArray<MetadataReference> References =
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
    /// Convenience: returns the generated source text for the single per-type
    /// output tree produced by the generator run (i.e. the emitted validator
    /// + binder file for the user's <c>[ConfigSection]</c> type).
    /// </summary>
    /// <remarks>Throws if no such file exists — tests that expect zero output should use <see cref="Run"/>.</remarks>
    public static string GetEmittedConfigSource(this GeneratorDriverRunResult result)
    {
        // Skip both post-init outputs (the attribute and the OptionsFactory
        // helper); only the per-type output is interesting to assertions.
        var emitted = result.NonPostInitGeneratedTrees().ToArray();

        if (emitted.Length == 0)
        {
            throw new System.InvalidOperationException("Generator produced no per-type output files.");
        }

        return emitted[0].ToString();
    }

    /// <summary>
    /// Returns every generated tree that is <em>not</em> a post-init output
    /// (i.e. excludes the attribute and the <c>ConfigBoundOptionsFactory</c>
    /// helper). Use this when a test wants to assert on what the generator
    /// produced for the user's annotated types.
    /// </summary>
    public static IEnumerable<Microsoft.CodeAnalysis.SyntaxTree> NonPostInitGeneratedTrees(this GeneratorDriverRunResult result)
    {
        foreach (var tree in result.GeneratedTrees)
        {
            var path = tree.FilePath;
            if (path.EndsWith(AttributeSource.HintName, System.StringComparison.Ordinal))
            {
                continue;
            }

            if (path.EndsWith(AttributeSource.OptionsFactoryHintName, System.StringComparison.Ordinal))
            {
                continue;
            }

            yield return tree;
        }
    }

    /// <summary>
    /// Compiles the supplied source through the generator, emits a fresh
    /// assembly into memory, loads it, builds an
    /// <see cref="IServiceCollection"/> with an in-memory configuration, and
    /// returns the bound options instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the canonical way to test the AOT-safe binding pipeline
    /// end-to-end. The test source declares a single
    /// <c>[ConfigSection("Test")]</c>-annotated type with whatever properties
    /// the test wants to exercise. The harness:
    /// </para>
    /// <list type="number">
    ///   <item><description>Runs <see cref="ConfigBoundGenerator"/> over the source.</description></item>
    ///   <item><description>Compiles the augmented compilation to a <see cref="MemoryStream"/>.</description></item>
    ///   <item><description>Loads the resulting assembly with <see cref="Assembly.Load(byte[])"/>.</description></item>
    ///   <item><description>Reflectively invokes the generated <c>AddTestConfig</c> extension.</description></item>
    ///   <item><description>Resolves <c>IOptions&lt;TestConfig&gt;.Value</c> and returns it as a plain <see cref="object"/>.</description></item>
    /// </list>
    /// <para>
    /// The reflection lives entirely in the test, never in the generated code.
    /// We use it here only because the test type is built dynamically; in a
    /// real consumer, every call site is statically resolved.
    /// </para>
    /// </remarks>
    /// <param name="source">C# source containing one <c>[ConfigSection]</c> type.</param>
    /// <param name="typeName">The simple name of the configured type (e.g. <c>"TestConfig"</c>).</param>
    /// <param name="configValues">Flat key/value pairs fed to a memory-backed <see cref="ConfigurationBuilder"/>.</param>
    /// <returns>The fully populated options instance.</returns>
    public static object CompileAndBind(string source, string typeName, IDictionary<string, string?> configValues)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);

        var compilation = CSharpCompilation.Create(
            assemblyName: "ConfigBoundNET.Tests.Bind." + System.Guid.NewGuid().ToString("N"),
            syntaxTrees: new[] { syntaxTree },
            references: References,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        // Run the generator and pull the augmented compilation back out so it
        // sees both the user's source and everything we emitted.
        var driver = CSharpGeneratorDriver.Create(
            generators: new[] { new ConfigBoundGenerator().AsSourceGenerator() },
            additionalTexts: ImmutableArray<AdditionalText>.Empty,
            parseOptions: parseOptions,
            optionsProvider: null);

        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var augmentedCompilation,
            out _);

        // Emit to memory. Any compile errors here are bugs in the generator,
        // so surface them with the C# error text rather than a generic message.
        using var peStream = new MemoryStream();
        var emitResult = augmentedCompilation.Emit(peStream);
        if (!emitResult.Success)
        {
            var errors = string.Join(
                System.Environment.NewLine,
                emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            throw new System.InvalidOperationException(
                "Generator output failed to compile:" + System.Environment.NewLine + errors);
        }

        peStream.Position = 0;
        var assembly = Assembly.Load(peStream.ToArray());

        // Build a real in-memory IConfiguration with the supplied values.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        // Locate the user's type and the generated extension class.
        var optionsType = assembly.GetTypes().Single(t => t.Name == typeName);
        var extensionsType = assembly.GetTypes().Single(t => t.Name == typeName + "ServiceCollectionExtensions");
        var addMethod = extensionsType.GetMethod("Add" + typeName, BindingFlags.Public | BindingFlags.Static)!;

        // Drive the standard DI / Options stack so we exercise the entire
        // OptionsFactory replacement path, not just the constructor.
        var services = new ServiceCollection();
        addMethod.Invoke(null, new object[] { services, configuration });
        var sp = services.BuildServiceProvider();

        var optionsClosed = typeof(IOptions<>).MakeGenericType(optionsType);
        var optionsInstance = sp.GetRequiredService(optionsClosed)!;
        var valueProperty = optionsClosed.GetProperty("Value")!;
        return valueProperty.GetValue(optionsInstance)!;
    }
}
