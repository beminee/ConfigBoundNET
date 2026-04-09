// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ConfigBoundNET;

// ─────────────────────────────────────────────────────────────────────────────
// Models
//
// These records are intentionally "flat": they contain only strings, bools, and
// EquatableArray<T>. They must never reference Roslyn symbols or syntax — those
// types are not value-equatable and would defeat incremental caching.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The fully hydrated model for a single <c>[ConfigSection]</c>-annotated type.
/// </summary>
/// <param name="TypeName">The simple type name (e.g. <c>DbConfig</c>).</param>
/// <param name="Namespace">The containing namespace, or <see langword="null"/> for the global namespace.</param>
/// <param name="SectionName">The configuration section name extracted from the attribute.</param>
/// <param name="Kind">Whether the target is a record class, plain class, or record struct.</param>
/// <param name="Properties">The set of properties that should be validated and bound.</param>
/// <param name="HintName">
/// A deterministic file name suffix used when calling <c>AddSource</c>. Two types
/// in different namespaces may share a simple name, so the hint encodes both.
/// </param>
internal sealed record ConfigSectionModel(
    string TypeName,
    string? Namespace,
    string SectionName,
    ConfigTypeKind Kind,
    EquatableArray<ConfigPropertyModel> Properties,
    string HintName);

/// <summary>
/// Minimal classification of the declared type so the emitter can pick the
/// right <c>partial record</c> / <c>partial class</c> keyword when extending it.
/// </summary>
internal enum ConfigTypeKind
{
    /// <summary>A regular <c>class</c>.</summary>
    Class,

    /// <summary>A <c>record</c> (reference type).</summary>
    Record,
}

/// <summary>
/// Represents a single bindable property on a <see cref="ConfigSectionModel"/>.
/// </summary>
/// <param name="Name">The property name (as declared in the source type).</param>
/// <param name="IsRequired">
/// <see langword="true"/> if the generator should emit a null/empty check for this property.
/// See <see cref="ModelBuilder.IsRequired"/> for the decision rules.
/// </param>
/// <param name="IsReferenceType">Whether the property type is a reference type (and therefore null-checkable).</param>
/// <param name="IsString">
/// Whether the property type is exactly <see cref="string"/>. Strings get a stricter
/// <c>IsNullOrWhiteSpace</c> check rather than a plain null check.
/// </param>
internal sealed record ConfigPropertyModel(
    string Name,
    bool IsRequired,
    bool IsReferenceType,
    bool IsString) : IEquatable<ConfigPropertyModel>;

/// <summary>
/// The output of <see cref="ModelBuilder.Build"/>. Diagnostics are represented as
/// equatable value records so they can flow through the incremental pipeline
/// without breaking output caching.
/// </summary>
internal sealed record BuildResult(
    ConfigSectionModel? Model,
    EquatableArray<DiagnosticInfo> Diagnostics);

/// <summary>
/// An equatable representation of a Roslyn <see cref="Diagnostic"/>.
/// </summary>
/// <remarks>
/// <see cref="Diagnostic"/> itself uses reference equality, so including it in
/// pipeline outputs would defeat incremental caching. We capture the minimum
/// information required to reconstruct the diagnostic and do so only inside
/// <c>RegisterSourceOutput</c>, where equality no longer matters.
/// </remarks>
internal sealed record DiagnosticInfo(
    string DescriptorId,
    LocationInfo? Location,
    EquatableArray<string> MessageArgs)
{
    /// <summary>Rehydrates a real Roslyn <see cref="Diagnostic"/> from this info.</summary>
    public Diagnostic ToDiagnostic()
    {
        var descriptor = DescriptorLookup.Get(DescriptorId);
        // Diagnostic.Create accepts a params object[] for messageArgs; EquatableArray<string>
        // is a value wrapper so we materialise it via AsArray() here.
        return Diagnostic.Create(descriptor, Location?.ToLocation(), messageArgs: MessageArgs.AsArray());
    }
}

/// <summary>
/// An equatable snapshot of a <see cref="Location"/> sufficient to rebuild it later.
/// </summary>
internal sealed record LocationInfo(string FilePath, Microsoft.CodeAnalysis.Text.TextSpan TextSpan, Microsoft.CodeAnalysis.Text.LinePositionSpan LineSpan)
{
    /// <summary>Rebuilds a Roslyn <see cref="Location"/> from the captured data.</summary>
    public Location ToLocation() => Location.Create(FilePath, TextSpan, LineSpan);

    /// <summary>Creates a <see cref="LocationInfo"/> from a syntax node, or returns null if no location is available.</summary>
    public static LocationInfo? From(SyntaxNode? node)
    {
        if (node is null)
        {
            return null;
        }

        var location = node.GetLocation();
        var sourceTree = location.SourceTree;
        if (sourceTree is null)
        {
            return null;
        }

        return new LocationInfo(sourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
    }
}

/// <summary>
/// Tiny lookup table used by <see cref="DiagnosticInfo.ToDiagnostic"/> to
/// resolve a <see cref="DiagnosticDescriptor"/> from its id at emission time.
/// </summary>
internal static class DescriptorLookup
{
    public static DiagnosticDescriptor Get(string id) => id switch
    {
        "CB0001" => DiagnosticDescriptors.MustBePartial,
        "CB0002" => DiagnosticDescriptors.EmptySectionName,
        "CB0003" => DiagnosticDescriptors.NestedTypeNotSupported,
        "CB0004" => DiagnosticDescriptors.NoBindableMembers,
        "CB0005" => DiagnosticDescriptors.InvalidTargetKind,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown diagnostic id."),
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// Builder
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Transforms a <see cref="GeneratorAttributeSyntaxContext"/> (i.e. a type the
/// Roslyn pipeline has already filtered down to things with
/// <c>[ConfigSection]</c>) into an equatable <see cref="BuildResult"/>.
/// </summary>
internal static class ModelBuilder
{
    /// <summary>
    /// Analyse the annotated type, collecting diagnostics and a model.
    /// </summary>
    /// <remarks>
    /// This method is called from the incremental pipeline. It must not touch
    /// anything that is not deterministic in the inputs, and the return value
    /// must be value-equatable.
    /// </remarks>
    public static BuildResult Build(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var symbol = (INamedTypeSymbol)context.TargetSymbol;
        var syntax = (TypeDeclarationSyntax)context.TargetNode;
        var diagnostics = new List<DiagnosticInfo>();

        // ── Must be a class or record. Structs are an anti-pattern for
        //    IOptions<T> (the framework hands out copies, defeating changes).
        if (symbol.TypeKind is not TypeKind.Class)
        {
            diagnostics.Add(new DiagnosticInfo(
                DiagnosticDescriptors.InvalidTargetKind.Id,
                LocationInfo.From(syntax),
                new EquatableArray<string>(new[] { symbol.Name })));
            return new BuildResult(null, new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));
        }

        // ── Must be partial so we can extend it.
        if (!syntax.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            diagnostics.Add(new DiagnosticInfo(
                DiagnosticDescriptors.MustBePartial.Id,
                LocationInfo.From(syntax),
                new EquatableArray<string>(new[] { symbol.Name })));
            return new BuildResult(null, new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));
        }

        // ── Must be top-level. See CB0003 rationale.
        if (symbol.ContainingType is not null)
        {
            diagnostics.Add(new DiagnosticInfo(
                DiagnosticDescriptors.NestedTypeNotSupported.Id,
                LocationInfo.From(syntax),
                new EquatableArray<string>(new[] { symbol.Name })));
            return new BuildResult(null, new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));
        }

        // ── Extract the section name from the attribute constructor.
        //    The Roslyn filter already guarantees the attribute is present,
        //    so Attributes[0] is safe.
        var attribute = context.Attributes[0];
        string? sectionName = null;
        if (attribute.ConstructorArguments.Length == 1 &&
            attribute.ConstructorArguments[0].Value is string s)
        {
            sectionName = s;
        }

        if (string.IsNullOrWhiteSpace(sectionName))
        {
            diagnostics.Add(new DiagnosticInfo(
                DiagnosticDescriptors.EmptySectionName.Id,
                LocationInfo.From(syntax),
                new EquatableArray<string>(new[] { symbol.Name })));
            return new BuildResult(null, new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));
        }

        // ── Collect bindable properties. We accept any public property with a
        //    setter or init accessor. Read-only properties cannot participate
        //    in configuration binding, so we silently skip them.
        var properties = new List<ConfigPropertyModel>();
        foreach (var member in symbol.GetMembers())
        {
            if (member is not IPropertySymbol property)
            {
                continue;
            }

            if (property.IsStatic || property.IsIndexer)
            {
                continue;
            }

            if (property.DeclaredAccessibility != Accessibility.Public)
            {
                continue;
            }

            if (property.SetMethod is null)
            {
                // A read-only property: not writable via binder.
                continue;
            }

            // For record types the compiler synthesises an EqualityContract
            // property. Filter it out — it is an implementation detail, not config.
            if (property.Name == "EqualityContract")
            {
                continue;
            }

            var isReferenceType = property.Type.IsReferenceType;
            var isString = property.Type.SpecialType == SpecialType.System_String;
            var isRequired = IsRequired(property);

            properties.Add(new ConfigPropertyModel(
                Name: property.Name,
                IsRequired: isRequired,
                IsReferenceType: isReferenceType,
                IsString: isString));
        }

        if (properties.Count == 0)
        {
            // Still emit the model — the extension method is useful even
            // for empty types — but warn the user something is probably wrong.
            diagnostics.Add(new DiagnosticInfo(
                DiagnosticDescriptors.NoBindableMembers.Id,
                LocationInfo.From(syntax),
                new EquatableArray<string>(new[] { symbol.Name })));
        }

        var ns = symbol.ContainingNamespace.IsGlobalNamespace
            ? null
            : symbol.ContainingNamespace.ToDisplayString();

        // HintName must be unique per compilation. Encode the namespace so
        // two types with the same simple name in different namespaces don't collide.
        var hint = ns is null
            ? $"Global.{symbol.Name}"
            : $"{ns.Replace('.', '_')}.{symbol.Name}";

        var model = new ConfigSectionModel(
            TypeName: symbol.Name,
            Namespace: ns,
            SectionName: sectionName!,
            Kind: symbol.IsRecord ? ConfigTypeKind.Record : ConfigTypeKind.Class,
            Properties: new EquatableArray<ConfigPropertyModel>(properties.ToArray()),
            HintName: hint);

        return new BuildResult(model, new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));
    }

    /// <summary>
    /// Decides whether a property should participate in the generated null-check
    /// logic emitted into <c>Validator.Validate</c>.
    /// </summary>
    /// <remarks>
    /// <para>A property is considered required when any of the following hold:</para>
    /// <list type="bullet">
    ///   <item><description>It carries the C# 11 <c>required</c> keyword.</description></item>
    ///   <item><description>It is a non-nullable reference type (or in a <c>#nullable disable</c> context, which we treat conservatively as non-null).</description></item>
    /// </list>
    /// <para>
    /// Value types are never marked required: the configuration binder always
    /// produces a defined default for them, so a null check is meaningless.
    /// Users who need value-type validation can layer DataAnnotations on top.
    /// </para>
    /// </remarks>
    private static bool IsRequired(IPropertySymbol property)
    {
        if (property.IsRequired)
        {
            return true;
        }

        if (!property.Type.IsReferenceType)
        {
            return false;
        }

        // NullableAnnotation.None → nullable context disabled → treat as non-null.
        // NullableAnnotation.NotAnnotated → explicit non-null.
        // NullableAnnotation.Annotated → explicit nullable (`string?`) → optional.
        return property.NullableAnnotation != NullableAnnotation.Annotated;
    }
}
