// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace ConfigBoundNET;

/// <summary>
/// The incremental Roslyn source generator that powers ConfigBoundNET.
/// </summary>
/// <remarks>
/// <para>Pipeline at a glance:</para>
/// <list type="number">
///   <item>
///     <description>
///       <b>Post-init</b> — emit the <c>[ConfigSection]</c> attribute itself as
///       C# source so user code can reference it with nothing more than a
///       package reference to ConfigBoundNET.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Syntax provider</b> — use
///       <see cref="SyntaxValueProvider.ForAttributeWithMetadataName"/> to find
///       every type decorated with <c>ConfigBoundNET.ConfigSectionAttribute</c>.
///       Roslyn handles the syntax filtering for us, which is dramatically faster
///       than walking every syntax node manually.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Transform</b> — convert each <see cref="GeneratorAttributeSyntaxContext"/>
///       into a value-equatable <see cref="BuildResult"/> via
///       <see cref="ModelBuilder.Build"/>. Only flat, equatable types pass
///       through here so that Roslyn can cache results between edits.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Source output</b> — for every <see cref="BuildResult"/>, report its
///       diagnostics and (if a valid model was produced) feed it into
///       <see cref="SourceEmitter.Emit"/> to materialise the generated code.
///     </description>
///   </item>
/// </list>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class ConfigBoundGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // ── Step 1: emit the attribute source plus the per-compilation
        //    OptionsFactory shim at post-initialization time. These run once
        //    per compilation, before any other generator work, and make both
        //    ConfigBoundNET.ConfigSectionAttribute and the
        //    ConfigBoundOptionsFactory<T> helper available to the user's
        //    source tree without requiring a runtime package reference.
        context.RegisterPostInitializationOutput(static ctx =>
        {
            ctx.AddSource(
                AttributeSource.HintName,
                SourceText.From(AttributeSource.Source, Encoding.UTF8));

            ctx.AddSource(
                AttributeSource.OptionsFactoryHintName,
                SourceText.From(AttributeSource.OptionsFactorySource, Encoding.UTF8));

            // [Sensitive] — opt-in property marker for ToString() redaction.
            // Same post-init emission as ConfigSectionAttribute so consumers
            // need no runtime package reference to use it.
            ctx.AddSource(
                AttributeSource.SensitiveAttributeHintName,
                SourceText.From(AttributeSource.SensitiveAttributeSource, Encoding.UTF8));
        });

        // ── Step 2: find every type annotated with [ConfigSection]. The
        //    ForAttributeWithMetadataName API does the heavy lifting — it
        //    uses Roslyn's indexed attribute lookup rather than a whole-syntax
        //    walk, which is the key to keeping incremental builds fast.
        //
        //    WithTrackingName labels this step so the cache/perf tests in
        //    GeneratorCacheTests can look it up via
        //    GeneratorDriverRunResult.TrackedSteps[TrackingNames.BuildResults]
        //    and assert that unrelated edits don't bust the cache.
        var buildResults = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: AttributeSource.FullyQualifiedMetadataName,
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, ct) => ModelBuilder.Build(ctx, ct))
            .WithTrackingName(TrackingNames.BuildResults);

        // ── Step 3: produce diagnostics and source for every result.
        context.RegisterSourceOutput(buildResults, static (productionContext, result) =>
        {
            // Report any diagnostics we collected during model building.
            // Doing this here (inside RegisterSourceOutput) means the Diagnostic
            // objects themselves never enter the cached part of the pipeline —
            // they are reconstructed on demand from the equatable DiagnosticInfo.
            foreach (var info in result.Diagnostics)
            {
                productionContext.ReportDiagnostic(info.ToDiagnostic());
            }

            // If the model could not be built (e.g. the type is not partial),
            // there is nothing more to emit. The diagnostic above tells the
            // user why.
            if (result.Model is null)
            {
                return;
            }

            var source = SourceEmitter.Emit(result.Model);
            productionContext.AddSource(
                hintName: $"{result.Model.HintName}.ConfigBound.g.cs",
                sourceText: SourceText.From(source, Encoding.UTF8));
        });

        // ── Step 4: project every successful model into a minimal
        //    AggregateEntry (namespace + type name only) and collect them into
        //    a single ordered array. The aggregate pipeline is deliberately
        //    isolated from per-type detail so that unrelated edits (e.g.
        //    adding a [Range] attribute) never re-emit the assembly-wide
        //    registration file.
        var aggregateEntries = buildResults
            .Select(static (result, _) =>
                result.Model is null
                    ? null
                    : new AggregateEntry(result.Model.Namespace, result.Model.TypeName))
            .Where(static entry => entry is not null)
            .Select(static (entry, _) => entry!)
            .WithTrackingName(TrackingNames.AggregateEntries);

        // Collect all entries into a single equatable array and sort them
        // deterministically (namespace then type name) so the generated file
        // is byte-stable across runs — snapshot tests rely on this.
        var aggregateCollected = aggregateEntries
            .Collect()
            .Select(static (entries, _) => entries
                .OrderBy(e => e.Namespace ?? string.Empty, System.StringComparer.Ordinal)
                .ThenBy(e => e.TypeName, System.StringComparer.Ordinal)
                .ToImmutableArray());

        // ── Step 5: emit one AddConfigBoundSections extension per compilation.
        //    Runs even when the array is empty — the method is then emitted
        //    with an empty body so callers can always write
        //    `services.AddConfigBoundSections(config)` without conditionals.
        context.RegisterSourceOutput(aggregateCollected, static (productionContext, entries) =>
        {
            var source = SourceEmitter.EmitAggregateRegistration(entries);
            productionContext.AddSource(
                hintName: "ConfigBoundNET.AggregateRegistration.g.cs",
                sourceText: SourceText.From(source, Encoding.UTF8));
        });
    }
}
