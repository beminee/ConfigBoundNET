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

        // ── Step 4: build the aggregate entry array.
        //    Classification of each entry's IsReferencedAsNested flag is a
        //    cross-type fact: we need to know what other [ConfigSection]
        //    types reference this one. That rules out the old "project each
        //    BuildResult independently" approach — we must .Collect() the
        //    full model set first, compute the nested-FQN set once, and
        //    then derive per-entry flags.
        //
        //    Cache behaviour stays friendly: the projection output is still
        //    an ImmutableArray<AggregateEntry> whose equality compares only
        //    (Namespace, TypeName, IsReferencedAsNested) triples. Per-type
        //    annotation edits don't move any triple, so the aggregate
        //    source output remains Unchanged — the existing
        //    Editing_unrelated_annotation_does_not_invalidate_aggregate
        //    test keeps passing. Adding a new nested reference DOES flip
        //    the target's flag, which is the correct invalidation.
        //    Wrap the transform output in EquatableArray<T> (rather than
        //    ImmutableArray<T>) so Roslyn's pipeline cache compares
        //    element-wise instead of by reference. Without the wrapper, a
        //    fresh ImmutableArray produced by this transform on every
        //    re-run would always report Modified — even when every entry
        //    is value-equal to the previous run — because
        //    EqualityComparer<ImmutableArray<T>>.Default resolves to the
        //    reference-based ObjectEqualityComparer.
        var aggregateCollected = buildResults
            .Select(static (result, _) => result.Model)
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect()
            .Select(static (models, _) =>
            {
                var nestedFqns = AggregateClassification.BuildNestedFqnSet(models);
                var entries = models
                    .Select(m => new AggregateEntry(
                        m.Namespace,
                        m.TypeName,
                        AggregateClassification.IsReferencedAsNested(m, nestedFqns)))
                    .OrderBy(e => e.Namespace ?? string.Empty, System.StringComparer.Ordinal)
                    .ThenBy(e => e.TypeName, System.StringComparer.Ordinal)
                    .ToArray();
                return new EquatableArray<AggregateEntry>(entries);
            })
            .WithTrackingName(TrackingNames.AggregateEntries);

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

        // ── Step 6: JSON-schema aggregate.
        //    Collect every successful ConfigSectionModel (not just
        //    AggregateEntry — the schema emitter needs the full per-type
        //    detail including properties, annotations, enum members, and
        //    nested FQNs). Sort by HintName for deterministic output, then
        //    feed the whole array into SchemaEmitter.Emit which produces a
        //    single .g.cs containing ConfigBoundJsonSchema.Json as a
        //    verbatim-string const.
        //
        //    This branch is a sibling of the aggregate pipeline, not a
        //    successor — both derive from buildResults independently so
        //    adding the schema step does not invalidate the aggregate
        //    step's cache (or vice versa).
        var schemaModels = buildResults
            .Select(static (result, _) => result.Model)
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect()
            .Select(static (models, _) => models
                .OrderBy(m => m.HintName, System.StringComparer.Ordinal)
                .ToImmutableArray())
            .WithTrackingName(TrackingNames.SchemaModels);

        context.RegisterSourceOutput(schemaModels, static (productionContext, models) =>
        {
            // Skip emission entirely when the compilation has no
            // [ConfigSection] types — avoids generating an empty-shell
            // ConfigBoundJsonSchema class no consumer would ever use.
            if (models.Length == 0)
            {
                return;
            }

            var schemaDiagnostics = new System.Collections.Generic.List<DiagnosticInfo>();
            var source = SchemaEmitter.Emit(models, schemaDiagnostics);

            foreach (var info in schemaDiagnostics)
            {
                productionContext.ReportDiagnostic(info.ToDiagnostic());
            }

            productionContext.AddSource(
                hintName: "ConfigBoundNET.JsonSchema.g.cs",
                sourceText: SourceText.From(source, Encoding.UTF8));
        });
    }
}
