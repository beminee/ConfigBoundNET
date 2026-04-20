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
/// Minimal equatable projection of a <see cref="ConfigSectionModel"/> used by
/// the assembly-wide <c>AddConfigBoundSections</c> emitter. Carries just the
/// type identity (namespace + simple name) that the aggregate registration
/// method needs to call <c>{Namespace}.{TypeName}ServiceCollectionExtensions
/// .Add{TypeName}(services, configuration)</c> via fully qualified syntax.
/// <para>
/// Deliberately excludes per-type detail (properties, DataAnnotations,
/// SectionName) so that edits to those fields do not invalidate the
/// aggregate pipeline's cache — adding a <c>[Range]</c> attribute on one
/// property in one <c>[ConfigSection]</c> type must not cause the
/// assembly-wide aggregate file to re-emit.
/// </para>
/// </summary>
internal sealed record AggregateEntry(string? Namespace, string TypeName);

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
/// <param name="Binding">
/// How the AOT-safe emitted binder should read this property out of an
/// <c>IConfigurationSection</c>. Drives <c>SourceEmitter.WriteBindMethod</c>.
/// </param>
/// <param name="IsNullableValueType">
/// <see langword="true"/> when the property is a <c>Nullable&lt;T&gt;</c>. Used by the
/// emitter so it can write <c>null</c> back when the configuration key is absent.
/// </param>
/// <param name="NestedTypeFullyQualifiedName">
/// For <see cref="BindingStrategy.NestedConfig"/> and
/// <see cref="BindingStrategy.NestedConfigCollection"/>, the fully qualified name
/// of the nested config type (which must itself be <c>[ConfigSection]</c>-annotated
/// and will therefore have its own generated <c>Bind</c> method to call into).
/// For the collection strategy this is the <em>element</em> type, not the
/// container type.
/// </param>
/// <param name="EnumFullyQualifiedName">
/// For <see cref="BindingStrategy.Enum"/>, the fully qualified enum type name passed
/// as the type argument to <c>System.Enum.TryParse&lt;T&gt;</c>.
/// </param>
/// <param name="ParseTypeKeyword">
/// For <see cref="BindingStrategy.Integer"/> and <see cref="BindingStrategy.FloatingPoint"/>,
/// the C# keyword (e.g. <c>int</c>, <c>long</c>, <c>double</c>) used to call the
/// matching static <c>TryParse</c> overload. Null for everything else.
/// </param>
/// <param name="DataAnnotations">
/// DataAnnotations attributes found on this property, parsed into flat
/// equatable models. The emitter consumes these to produce explicit,
/// reflection-free validation checks after the existing null/whitespace pass.
/// Empty when no recognised annotations are present.
/// </param>
/// <param name="CollectionElementStrategy">
/// For <see cref="BindingStrategy.Array"/> and <see cref="BindingStrategy.Dictionary"/>:
/// how to parse each element value from <c>IConfigurationSection.GetChildren()</c>.
/// <see cref="BindingStrategy.Unsupported"/> for non-collection properties.
/// </param>
/// <param name="CollectionElementKeyword">
/// For collections: the C# keyword or fully qualified name of the element/value
/// type, used to emit the per-element <c>TryParse</c> call and the generic
/// <c>List&lt;T&gt;</c> / <c>Dictionary&lt;string, T&gt;</c> container.
/// </param>
/// <param name="IsCollectionArray">
/// <see langword="true"/> when the property type is <c>T[]</c> (needs <c>.ToArray()</c>
/// after building the list); <see langword="false"/> for <c>List&lt;T&gt;</c> and interfaces.
/// </param>
/// <param name="IsSensitive">
/// <see langword="true"/> when the property is decorated with
/// <c>[Sensitive]</c>. Causes the emitter to redact this property's value to
/// <c>"***"</c> in the generated <c>PrintMembers</c> (records) or
/// <c>ToString</c> (classes) override, provided at least one property on the
/// type is sensitive. Default <see langword="false"/> — types with zero
/// sensitive properties emit no redacted override and keep the
/// compiler-synthesized record <c>ToString</c>.
/// </param>
/// <param name="EnumMemberNames">
/// For enum-typed properties (scalar or collection-of-enum element): the
/// declared enum member names in declaration order. Consumed by the
/// JSON-schema emitter to produce <c>"enum":[...]</c> fragments so editors
/// can offer IntelliSense on the allowed values. Empty for non-enum
/// properties. The runtime binder ignores this — it still calls
/// <c>Enum.TryParse&lt;T&gt;(ignoreCase:true)</c>, which accepts any declared
/// member.
/// </param>
internal sealed record ConfigPropertyModel(
    string Name,
    bool IsRequired,
    bool IsReferenceType,
    bool IsString,
    BindingStrategy Binding,
    bool IsNullableValueType,
    string? NestedTypeFullyQualifiedName,
    string? EnumFullyQualifiedName,
    string? ParseTypeKeyword,
    EquatableArray<DataAnnotationModel> DataAnnotations,
    BindingStrategy CollectionElementStrategy,
    string? CollectionElementKeyword,
    bool IsCollectionArray,
    bool IsSensitive,
    EquatableArray<string> EnumMemberNames) : IEquatable<ConfigPropertyModel>;

/// <summary>
/// How the generated, reflection-free binder should read a property out of an
/// <c>IConfigurationSection</c>.
/// </summary>
/// <remarks>
/// Each value maps one-to-one to a branch inside
/// <see cref="SourceEmitter"/>'s <c>WriteBindAssignment</c>. Adding a new
/// supported type means: extend this enum, teach
/// <see cref="ModelBuilder.ClassifyBinding"/> to recognise the symbol, and
/// teach the emitter to render the assignment.
/// </remarks>
internal enum BindingStrategy
{
    /// <summary>The property type is not in the supported set; CB0010 is raised and binding is skipped.</summary>
    Unsupported = 0,

    /// <summary><see cref="string"/> — copied verbatim from <c>section["X"]</c>.</summary>
    String,

    /// <summary><see cref="bool"/> via <see cref="bool.TryParse(string, out bool)"/>.</summary>
    Boolean,

    /// <summary><see cref="byte"/> / <see cref="sbyte"/> / <see cref="short"/> / <see cref="ushort"/> / <see cref="int"/> / <see cref="uint"/> / <see cref="long"/> / <see cref="ulong"/>.</summary>
    Integer,

    /// <summary><see cref="float"/> / <see cref="double"/> / <see cref="decimal"/>.</summary>
    FloatingPoint,

    /// <summary><see cref="System.Guid"/> via <see cref="System.Guid.TryParse(string, out System.Guid)"/>.</summary>
    Guid,

    /// <summary><see cref="System.TimeSpan"/> via <see cref="System.TimeSpan.TryParse(string, System.IFormatProvider, out System.TimeSpan)"/>.</summary>
    TimeSpan,

    /// <summary><see cref="System.DateTime"/> via <see cref="System.DateTime.TryParse(string, System.IFormatProvider, System.Globalization.DateTimeStyles, out System.DateTime)"/>.</summary>
    DateTime,

    /// <summary><see cref="System.DateTimeOffset"/>.</summary>
    DateTimeOffset,

    /// <summary><see cref="System.Uri"/> via <see cref="System.Uri.TryCreate(string, System.UriKind, out System.Uri)"/>.</summary>
    Uri,

    /// <summary>Any user-defined enum — bound via <c>System.Enum.TryParse&lt;TEnum&gt;</c> which is AOT-safe.</summary>
    Enum,

    /// <summary>A nested complex config type that is itself <c>[ConfigSection]</c>-annotated; bound by recursing into its generated <c>Bind</c> method.</summary>
    NestedConfig,

    /// <summary>Array (<c>T[]</c>) or list-like collection (<c>List&lt;T&gt;</c>, <c>IList&lt;T&gt;</c>, etc.); elements parsed individually from <c>GetChildren()</c>.</summary>
    Array,

    /// <summary><c>Dictionary&lt;string, T&gt;</c> or <c>IDictionary&lt;string, T&gt;</c>; keys from <c>child.Key</c>, values parsed individually.</summary>
    Dictionary,

    /// <summary>
    /// A collection (<c>T[]</c>, <c>List&lt;T&gt;</c>, <c>IReadOnlyList&lt;T&gt;</c>, etc.)
    /// whose element type is itself <c>[ConfigSection]</c>-annotated. Each child
    /// section is passed to the element type's generated
    /// <c>(IConfigurationSection)</c> constructor. <c>NestedTypeFullyQualifiedName</c>
    /// carries the element FQN and <c>IsCollectionArray</c> disambiguates
    /// <c>T[]</c> from <c>List&lt;T&gt;</c> / interfaces.
    /// </summary>
    NestedConfigCollection,

    /// <summary>
    /// A dictionary (<c>Dictionary&lt;string, T&gt;</c>, <c>IDictionary&lt;string, T&gt;</c>,
    /// <c>IReadOnlyDictionary&lt;string, T&gt;</c>) whose <em>value</em> type is
    /// itself <c>[ConfigSection]</c>-annotated. The string key comes from each
    /// child's <c>IConfigurationSection.Key</c>; the value is built via the
    /// element type's generated <c>(IConfigurationSection)</c> constructor.
    /// <c>NestedTypeFullyQualifiedName</c> carries the value type's FQN.
    /// </summary>
    NestedConfigDictionary,
}

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
        "CB0006" => DiagnosticDescriptors.RangeOnNonNumeric,
        "CB0007" => DiagnosticDescriptors.LengthOnNonString,
        "CB0008" => DiagnosticDescriptors.InvalidRegexPattern,
        "CB0009" => DiagnosticDescriptors.RedundantRequired,
        "CB0010" => DiagnosticDescriptors.UnsupportedBindingType,
        "CB0011" => DiagnosticDescriptors.ExternalNestedConfigNotAnalyzed,
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

        if (attribute.ConstructorArguments.Length == 0)
        {
            // Parameterless [ConfigSection] — infer the section name from
            // the type name by stripping common suffixes (Config, Options, etc.).
            sectionName = SectionNameHelper.InferSectionName(symbol.Name);
        }
        else if (attribute.ConstructorArguments.Length == 1 &&
                 attribute.ConstructorArguments[0].Value is string s)
        {
            sectionName = s;
        }

        // If the user explicitly passed an empty/whitespace string via
        // [ConfigSection("")], that's a mistake — error with CB0002.
        // The parameterless path above always produces a non-empty name
        // so this only fires for the explicit-string constructor.
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
        //    Pre-sized to the total member count: a safe upper bound (most
        //    members *are* properties, and non-property members are cheap to
        //    skip). Single correctly-sized backing array; no list-growth
        //    doubling mid-loop. Trims allocations on the incremental path.
        var properties = new List<ConfigPropertyModel>(capacity: symbol.GetMembers().Length);
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
            var isSensitive = HasSensitiveAttribute(property);

            // Classify how this property will be bound. Unsupported types
            // produce a CB0010 warning and are skipped at emit time, so the
            // user still gets a working binder for the rest of the type.
            var classification = ClassifyBinding(property.Type);

            if (classification.Strategy == BindingStrategy.Unsupported)
            {
                diagnostics.Add(new DiagnosticInfo(
                    DiagnosticDescriptors.UnsupportedBindingType.Id,
                    LocationInfo.From(syntax),
                    new EquatableArray<string>(new[] { property.Name, property.Type.ToDisplayString() })));
            }

            // Scan for DataAnnotations attributes and convert them into
            // flat, equatable models the emitter can turn into explicit checks.
            var isCollection = classification.Strategy is BindingStrategy.Array or BindingStrategy.Dictionary or BindingStrategy.NestedConfigCollection or BindingStrategy.NestedConfigDictionary;
            var annotations = ExtractDataAnnotations(
                property, classification.Strategy, isString, isRequired, isCollection,
                diagnostics, syntax);

            // Capture enum member names for scalar enum properties and for
            // collection-of-enum element types. Consumed by the JSON-schema
            // emitter; the runtime binder ignores it (still uses TryParse).
            var enumMembers = ExtractEnumMemberNames(property.Type, classification);

            properties.Add(new ConfigPropertyModel(
                Name: property.Name,
                IsRequired: isRequired,
                IsReferenceType: isReferenceType,
                IsString: isString,
                Binding: classification.Strategy,
                IsNullableValueType: classification.IsNullableValueType,
                NestedTypeFullyQualifiedName: classification.NestedTypeFullyQualifiedName,
                EnumFullyQualifiedName: classification.EnumFullyQualifiedName,
                ParseTypeKeyword: classification.ParseTypeKeyword,
                // Zero-annotation case takes the `default` struct value (no
                // backing array allocated). EquatableArray<T>.AsArray() /
                // Length already treat null-backed instances as empty, so
                // equality and downstream enumeration behave identically to
                // an explicitly-constructed empty instance.
                DataAnnotations: annotations is null || annotations.Count == 0
                    ? default
                    : new EquatableArray<DataAnnotationModel>(annotations.ToArray()),
                CollectionElementStrategy: classification.CollectionElementStrategy,
                CollectionElementKeyword: classification.CollectionElementKeyword,
                IsCollectionArray: classification.IsCollectionArray,
                IsSensitive: isSensitive,
                EnumMemberNames: enumMembers));
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

    /// <summary>
    /// Decides how the reflection-free binder should populate a property of the
    /// given type, returning a flat record so the result can flow into the
    /// equatable property model without retaining any Roslyn symbols.
    /// </summary>
    /// <remarks>
    /// <para>The classifier is the only place that touches Roslyn types when
    /// figuring out the binding strategy. Once it returns, the rest of the
    /// pipeline operates on plain strings.</para>
    /// <para>Returns <see cref="BindingStrategy.Unsupported"/> for any type
    /// the emitter does not yet know how to bind — the caller raises
    /// <see cref="DiagnosticDescriptors.UnsupportedBindingType"/> and falls
    /// through. Adding new supported types is intentionally a one-line change
    /// in this method plus a matching branch in <see cref="SourceEmitter"/>.</para>
    /// </remarks>
    private static BindingClassification ClassifyBinding(ITypeSymbol type)
    {
        // Strip Nullable<T> first so we classify the underlying value type and
        // simply remember that the property accepts null.
        var isNullableValueType = false;
        if (type is INamedTypeSymbol named &&
            named.IsGenericType &&
            named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            isNullableValueType = true;
            type = named.TypeArguments[0];
        }

        // ── Primitive / well-known BCL types ──────────────────────────────
        switch (type.SpecialType)
        {
            case SpecialType.System_String:
                return new BindingClassification(BindingStrategy.String, false, null, null, null, BindingStrategy.Unsupported, null, false);

            case SpecialType.System_Boolean:
                return new BindingClassification(BindingStrategy.Boolean, isNullableValueType, null, null, "bool", BindingStrategy.Unsupported, null, false);

            case SpecialType.System_Byte:
                return new BindingClassification(BindingStrategy.Integer, isNullableValueType, null, null, "byte", BindingStrategy.Unsupported, null, false);
            case SpecialType.System_SByte:
                return new BindingClassification(BindingStrategy.Integer, isNullableValueType, null, null, "sbyte", BindingStrategy.Unsupported, null, false);
            case SpecialType.System_Int16:
                return new BindingClassification(BindingStrategy.Integer, isNullableValueType, null, null, "short", BindingStrategy.Unsupported, null, false);
            case SpecialType.System_UInt16:
                return new BindingClassification(BindingStrategy.Integer, isNullableValueType, null, null, "ushort", BindingStrategy.Unsupported, null, false);
            case SpecialType.System_Int32:
                return new BindingClassification(BindingStrategy.Integer, isNullableValueType, null, null, "int", BindingStrategy.Unsupported, null, false);
            case SpecialType.System_UInt32:
                return new BindingClassification(BindingStrategy.Integer, isNullableValueType, null, null, "uint", BindingStrategy.Unsupported, null, false);
            case SpecialType.System_Int64:
                return new BindingClassification(BindingStrategy.Integer, isNullableValueType, null, null, "long", BindingStrategy.Unsupported, null, false);
            case SpecialType.System_UInt64:
                return new BindingClassification(BindingStrategy.Integer, isNullableValueType, null, null, "ulong", BindingStrategy.Unsupported, null, false);

            case SpecialType.System_Single:
                return new BindingClassification(BindingStrategy.FloatingPoint, isNullableValueType, null, null, "float", BindingStrategy.Unsupported, null, false);
            case SpecialType.System_Double:
                return new BindingClassification(BindingStrategy.FloatingPoint, isNullableValueType, null, null, "double", BindingStrategy.Unsupported, null, false);
            case SpecialType.System_Decimal:
                return new BindingClassification(BindingStrategy.FloatingPoint, isNullableValueType, null, null, "decimal", BindingStrategy.Unsupported, null, false);
        }

        // ── Enums (matched before the named-type sniff so System.Enum subtypes win). ──
        if (type.TypeKind == TypeKind.Enum)
        {
            return new BindingClassification(
                BindingStrategy.Enum,
                isNullableValueType,
                NestedTypeFullyQualifiedName: null,
                EnumFullyQualifiedName: type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                ParseTypeKeyword: null,
                CollectionElementStrategy: BindingStrategy.Unsupported,
                CollectionElementKeyword: null,
                IsCollectionArray: false);
        }

        // ── Named non-special types: Guid, TimeSpan, DateTime, Uri, nested config. ──
        var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        switch (fullName)
        {
            case "global::System.Guid":
                return new BindingClassification(BindingStrategy.Guid, isNullableValueType, null, null, null, BindingStrategy.Unsupported, null, false);

            case "global::System.TimeSpan":
                return new BindingClassification(BindingStrategy.TimeSpan, isNullableValueType, null, null, null, BindingStrategy.Unsupported, null, false);

            case "global::System.DateTime":
                return new BindingClassification(BindingStrategy.DateTime, isNullableValueType, null, null, null, BindingStrategy.Unsupported, null, false);

            case "global::System.DateTimeOffset":
                return new BindingClassification(BindingStrategy.DateTimeOffset, isNullableValueType, null, null, null, BindingStrategy.Unsupported, null, false);

            case "global::System.Uri":
                // Uri is a reference type, so the "nullable" form is just `Uri?`,
                // not Nullable<Uri>. isNullableValueType therefore stays false.
                return new BindingClassification(BindingStrategy.Uri, false, null, null, null, BindingStrategy.Unsupported, null, false);
        }

        // ── Nested complex configuration types. We accept any user-defined
        //    class or record annotated with [ConfigSection]: it will have its
        //    own generated Bind method we can call into. Anything else is
        //    classified Unsupported and produces CB0010.
        if (type is INamedTypeSymbol nestedNamed &&
            (nestedNamed.TypeKind == TypeKind.Class) &&
            HasConfigSectionAttribute(nestedNamed))
        {
            return new BindingClassification(
                BindingStrategy.NestedConfig,
                IsNullableValueType: false,
                NestedTypeFullyQualifiedName: nestedNamed.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                EnumFullyQualifiedName: null,
                ParseTypeKeyword: null,
                CollectionElementStrategy: BindingStrategy.Unsupported,
                CollectionElementKeyword: null,
                IsCollectionArray: false);
        }

        // ── Arrays (T[]) ──────────────────────────────────────────────────
        if (type is IArrayTypeSymbol arrayType && arrayType.Rank == 1)
        {
            // Complex-element collection takes priority over scalar ClassifyElement:
            // an element type that is itself [ConfigSection]-annotated is bound
            // via its own generated (IConfigurationSection) constructor, not via
            // scalar TryParse. Without this branch the element would fall through
            // to Unsupported and the whole property would raise CB0010.
            if (TryClassifyNestedElement(arrayType.ElementType, out var arrElemFqn))
            {
                return new BindingClassification(
                    BindingStrategy.NestedConfigCollection, false,
                    NestedTypeFullyQualifiedName: arrElemFqn,
                    EnumFullyQualifiedName: null,
                    ParseTypeKeyword: null,
                    CollectionElementStrategy: BindingStrategy.Unsupported,
                    CollectionElementKeyword: null,
                    IsCollectionArray: true);
            }

            var elem = ClassifyElement(arrayType.ElementType);
            if (elem.Strategy != BindingStrategy.Unsupported)
            {
                return new BindingClassification(
                    BindingStrategy.Array, false, null, null, null,
                    elem.Strategy, elem.Keyword, IsCollectionArray: true);
            }
        }

        // ── List<T>, IList<T>, ICollection<T>, IEnumerable<T>, etc. ──────
        if (type is INamedTypeSymbol { IsGenericType: true } genericType)
        {
            var origDef = genericType.OriginalDefinition.ToDisplayString();

            if (genericType.TypeArguments.Length == 1 && IsListLikeInterface(origDef))
            {
                // Same priority rule as the array branch above: complex-element
                // collections have to be intercepted before ClassifyElement sees
                // the type, otherwise the property falls through to Unsupported.
                if (TryClassifyNestedElement(genericType.TypeArguments[0], out var listElemFqn))
                {
                    return new BindingClassification(
                        BindingStrategy.NestedConfigCollection, false,
                        NestedTypeFullyQualifiedName: listElemFqn,
                        EnumFullyQualifiedName: null,
                        ParseTypeKeyword: null,
                        CollectionElementStrategy: BindingStrategy.Unsupported,
                        CollectionElementKeyword: null,
                        IsCollectionArray: false);
                }

                var elem = ClassifyElement(genericType.TypeArguments[0]);
                if (elem.Strategy != BindingStrategy.Unsupported)
                {
                    return new BindingClassification(
                        BindingStrategy.Array, false, null, null, null,
                        elem.Strategy, elem.Keyword, IsCollectionArray: false);
                }
            }

            // ── Dictionary<string, T>, IDictionary<string, T>, etc. ──────
            if (genericType.TypeArguments.Length == 2 && IsDictionaryLikeInterface(origDef))
            {
                // Only string keys are supported; IConfiguration child keys are always strings.
                if (genericType.TypeArguments[0].SpecialType == SpecialType.System_String)
                {
                    // Complex-value dictionary takes priority over scalar
                    // ClassifyElement: a value type that is itself
                    // [ConfigSection]-annotated is bound via its own generated
                    // (IConfigurationSection) constructor, not via scalar
                    // TryParse. Without this branch the value would fall
                    // through to Unsupported and the property would raise CB0010.
                    if (TryClassifyNestedElement(genericType.TypeArguments[1], out var dictValueFqn))
                    {
                        return new BindingClassification(
                            BindingStrategy.NestedConfigDictionary, false,
                            NestedTypeFullyQualifiedName: dictValueFqn,
                            EnumFullyQualifiedName: null,
                            ParseTypeKeyword: null,
                            CollectionElementStrategy: BindingStrategy.Unsupported,
                            CollectionElementKeyword: null,
                            IsCollectionArray: false);
                    }

                    var elem = ClassifyElement(genericType.TypeArguments[1]);
                    if (elem.Strategy != BindingStrategy.Unsupported)
                    {
                        return new BindingClassification(
                            BindingStrategy.Dictionary, false, null, null, null,
                            elem.Strategy, elem.Keyword, IsCollectionArray: false);
                    }
                }
            }
        }

        return new BindingClassification(BindingStrategy.Unsupported, false, null, null, null, BindingStrategy.Unsupported, null, false);
    }

    /// <summary>
    /// Classifies an element type for use inside a collection. Returns the
    /// <see cref="BindingStrategy"/> and a keyword/FQN for the element.
    /// Only supports scalar types (no nested collections or complex types).
    /// </summary>
    private static (BindingStrategy Strategy, string? Keyword) ClassifyElement(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_String:  return (BindingStrategy.String, null);
            case SpecialType.System_Boolean: return (BindingStrategy.Boolean, "bool");
            case SpecialType.System_Byte:    return (BindingStrategy.Integer, "byte");
            case SpecialType.System_SByte:   return (BindingStrategy.Integer, "sbyte");
            case SpecialType.System_Int16:   return (BindingStrategy.Integer, "short");
            case SpecialType.System_UInt16:  return (BindingStrategy.Integer, "ushort");
            case SpecialType.System_Int32:   return (BindingStrategy.Integer, "int");
            case SpecialType.System_UInt32:  return (BindingStrategy.Integer, "uint");
            case SpecialType.System_Int64:   return (BindingStrategy.Integer, "long");
            case SpecialType.System_UInt64:  return (BindingStrategy.Integer, "ulong");
            case SpecialType.System_Single:  return (BindingStrategy.FloatingPoint, "float");
            case SpecialType.System_Double:  return (BindingStrategy.FloatingPoint, "double");
            case SpecialType.System_Decimal: return (BindingStrategy.FloatingPoint, "decimal");
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return (BindingStrategy.Enum, type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        }

        var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return fullName switch
        {
            "global::System.Guid"           => (BindingStrategy.Guid, null),
            "global::System.TimeSpan"       => (BindingStrategy.TimeSpan, null),
            "global::System.DateTime"       => (BindingStrategy.DateTime, null),
            "global::System.DateTimeOffset" => (BindingStrategy.DateTimeOffset, null),
            "global::System.Uri"            => (BindingStrategy.Uri, null),
            _                               => (BindingStrategy.Unsupported, null),
        };
    }

    /// <summary>Returns true for List-like generic type definitions.</summary>
    private static bool IsListLikeInterface(string originalDefinition) => originalDefinition is
        "System.Collections.Generic.List<T>" or
        "System.Collections.Generic.IList<T>" or
        "System.Collections.Generic.ICollection<T>" or
        "System.Collections.Generic.IEnumerable<T>" or
        "System.Collections.Generic.IReadOnlyList<T>" or
        "System.Collections.Generic.IReadOnlyCollection<T>";

    /// <summary>Returns true for Dictionary-like generic type definitions.</summary>
    private static bool IsDictionaryLikeInterface(string originalDefinition) => originalDefinition is
        "System.Collections.Generic.Dictionary<TKey, TValue>" or
        "System.Collections.Generic.IDictionary<TKey, TValue>" or
        "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>";

    /// <summary>
    /// Returns true iff <paramref name="elementType"/> is a class or record
    /// annotated with <c>[ConfigSection]</c>, and reports its fully qualified
    /// name. Used to classify collections whose element type is itself a
    /// nested config (<c>List&lt;EndpointConfig&gt;</c>, <c>EndpointConfig[]</c>,
    /// etc.).
    /// <para>
    /// The reported FQN <em>preserves</em> nullable reference annotations
    /// (<c>T?</c>) so the emitted internal container (e.g. <c>List&lt;T?&gt;</c>)
    /// matches the user's declared property type. Without this, assigning a
    /// <c>List&lt;T&gt;</c> to a <c>List&lt;T?&gt;</c> property would fail type-check
    /// under strict covariance. Semantically the binder still never produces
    /// null elements; the annotation is preserved for the type system, not
    /// because nulls are expected.
    /// </para>
    /// </summary>
    private static bool TryClassifyNestedElement(ITypeSymbol elementType, out string? fullyQualifiedName)
    {
        if (elementType is INamedTypeSymbol named &&
            named.TypeKind == TypeKind.Class &&
            HasConfigSectionAttribute(named))
        {
            // IncludeNullableReferenceTypeModifier preserves the `?` that the
            // user wrote on the element (e.g. List<EndpointConfig?> keeps the
            // `?` in the FQN). FullyQualifiedFormat alone drops it.
            fullyQualifiedName = named.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
                    .WithMiscellaneousOptions(
                        SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
                        | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
            return true;
        }

        fullyQualifiedName = null;
        return false;
    }

    /// <summary>
    /// Extracts the declared enum member names from a scalar-enum or
    /// collection-of-enum property. Returns an empty
    /// <see cref="EquatableArray{T}"/> for everything else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only consumed by the JSON-schema emitter: the runtime binder still
    /// calls <c>Enum.TryParse&lt;T&gt;(ignoreCase: true)</c>, which accepts
    /// any declared member regardless of this list. We capture names rather
    /// than numeric values because JSON config authors typically write the
    /// member name, and the schema <c>enum</c> fragment must match that
    /// spelling.
    /// </para>
    /// <para>
    /// For nullable-value enums (<c>MyEnum?</c>) the underlying enum is
    /// unwrapped from <c>Nullable&lt;T&gt;</c>. For collection-of-enum
    /// (<c>List&lt;MyEnum&gt;</c>, <c>MyEnum[]</c>, <c>Dictionary&lt;string,
    /// MyEnum&gt;</c>) the element-type symbol is reached through the type
    /// arguments of the collection; the classifier has already confirmed
    /// <see cref="BindingClassification.CollectionElementStrategy"/> equals
    /// <see cref="BindingStrategy.Enum"/>, so the cast is safe.
    /// </para>
    /// </remarks>
    private static EquatableArray<string> ExtractEnumMemberNames(ITypeSymbol propertyType, BindingClassification classification)
    {
        ITypeSymbol? enumType = null;

        if (classification.Strategy == BindingStrategy.Enum)
        {
            enumType = propertyType is INamedTypeSymbol nullable
                && nullable.IsGenericType
                && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                ? nullable.TypeArguments[0]
                : propertyType;
        }
        else if (classification.CollectionElementStrategy == BindingStrategy.Enum)
        {
            if (propertyType is IArrayTypeSymbol arr)
            {
                enumType = arr.ElementType;
            }
            else if (propertyType is INamedTypeSymbol generic && generic.IsGenericType)
            {
                // Dictionary<string, T> picks TypeArguments[1]; everything
                // else (List<T>, IList<T>, …) picks TypeArguments[0].
                var index = classification.Strategy == BindingStrategy.Dictionary ? 1 : 0;
                if (generic.TypeArguments.Length > index)
                {
                    enumType = generic.TypeArguments[index];
                }
            }
        }

        if (enumType is null || enumType.TypeKind != TypeKind.Enum)
        {
            return default;
        }

        var members = new List<string>();
        foreach (var member in enumType.GetMembers())
        {
            if (member is IFieldSymbol field && field.HasConstantValue)
            {
                members.Add(field.Name);
            }
        }

        return members.Count == 0
            ? default
            : new EquatableArray<string>(members.ToArray());
    }

    /// <summary>Returns true when <paramref name="type"/> declares a [ConfigSection] attribute.</summary>
    private static bool HasConfigSectionAttribute(INamedTypeSymbol type)
    {
        foreach (var attr in type.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == "ConfigBoundNET.ConfigSectionAttribute")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the property carries
    /// <c>[Sensitive]</c>. Drives the emitter's decision to produce a
    /// redacted <c>PrintMembers</c> / <c>ToString</c> override.
    /// </summary>
    private static bool HasSensitiveAttribute(IPropertySymbol property)
    {
        foreach (var attr in property.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == "ConfigBoundNET.SensitiveAttribute")
            {
                return true;
            }
        }

        return false;
    }

    // ── DataAnnotations extraction ──────────────────────────────────────

    /// <summary>
    /// The <c>System.ComponentModel.DataAnnotations</c> namespace prefix used
    /// to match annotation attribute classes by their fully qualified name.
    /// </summary>
    private const string DaNamespace = "System.ComponentModel.DataAnnotations.";

    /// <summary>
    /// Scans the attributes on <paramref name="property"/> for recognised
    /// DataAnnotations, converts each into a flat
    /// <see cref="DataAnnotationModel"/>, and emits diagnostics when an
    /// annotation is misapplied (wrong target type, invalid regex, etc.).
    /// </summary>
    private static List<DataAnnotationModel>? ExtractDataAnnotations(
        IPropertySymbol property,
        BindingStrategy binding,
        bool isString,
        bool isRequired,
        bool isCollection,
        List<DiagnosticInfo> diagnostics,
        TypeDeclarationSyntax syntax)
    {
        // Start null; allocate on first matched annotation. Most user
        // properties carry zero DataAnnotations, so zero allocation is
        // the common case. Returning null means "no annotations" — the
        // caller maps that to default(EquatableArray<T>) and skips the
        // empty-array allocation entirely.
        List<DataAnnotationModel>? result = null;
        var location = LocationInfo.From(syntax);
        var isNumeric = binding is BindingStrategy.Integer or BindingStrategy.FloatingPoint;
        var typeName = property.Type.ToDisplayString();

        foreach (var attr in property.GetAttributes())
        {
            var fqn = attr.AttributeClass?.ToDisplayString();
            if (fqn is null || !fqn.StartsWith(DaNamespace, StringComparison.Ordinal))
            {
                continue;
            }

            // Strip the namespace prefix to get a simple switch key.
            var simpleName = fqn.Substring(DaNamespace.Length);

            // Read the ErrorMessage named argument if the user supplied one.
            // All ValidationAttribute-derived attributes inherit this property,
            // so we read it uniformly for every matched attribute.
            string? errorMessage = null;
            foreach (var named in attr.NamedArguments)
            {
                if (named.Key == "ErrorMessage" && named.Value.Value is string em)
                {
                    errorMessage = em;
                    break;
                }
            }

            switch (simpleName)
            {
                case "RequiredAttribute":
                    if (isRequired)
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            DiagnosticDescriptors.RedundantRequired.Id,
                            location,
                            new EquatableArray<string>(new[] { property.Name })));
                    }

                    // Still emit the model so the validation is present even
                    // when the user explicitly opted in with [Required].
                    (result ??= new List<DataAnnotationModel>()).Add(new DataAnnotationModel(
                        DataAnnotationKind.Required,
                        null, null, null,
                        new EquatableArray<string>(Array.Empty<string>()),
                        errorMessage));
                    break;

                case "RangeAttribute":
                    if (!isNumeric)
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            DiagnosticDescriptors.RangeOnNonNumeric.Id,
                            location,
                            new EquatableArray<string>(new[] { property.Name, typeName })));
                        break;
                    }

                    if (attr.ConstructorArguments.Length >= 2)
                    {
                        var min = ConvertToDouble(attr.ConstructorArguments[0]);
                        var max = ConvertToDouble(attr.ConstructorArguments[1]);
                        if (min.HasValue && max.HasValue)
                        {
                            (result ??= new List<DataAnnotationModel>()).Add(new DataAnnotationModel(
                                DataAnnotationKind.Range,
                                null, min.Value, max.Value,
                                new EquatableArray<string>(Array.Empty<string>()),
                        errorMessage));
                        }
                    }
                    break;

                case "StringLengthAttribute":
                    if (!isString && !isCollection)
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            DiagnosticDescriptors.LengthOnNonString.Id,
                            location,
                            new EquatableArray<string>(new[] { "StringLength", property.Name, typeName })));
                        break;
                    }

                    if (attr.ConstructorArguments.Length >= 1)
                    {
                        var maxLen = ConvertToDouble(attr.ConstructorArguments[0]);
                        double? minLen = null;
                        // MinimumLength is a named argument, not a constructor arg.
                        foreach (var named in attr.NamedArguments)
                        {
                            if (named.Key == "MinimumLength")
                            {
                                minLen = ConvertToDouble(named.Value);
                            }
                        }

                        (result ??= new List<DataAnnotationModel>()).Add(new DataAnnotationModel(
                            DataAnnotationKind.StringLength,
                            null, maxLen, minLen,
                            new EquatableArray<string>(Array.Empty<string>()),
                        errorMessage));
                    }
                    break;

                case "MinLengthAttribute":
                    if (!isString && !isCollection)
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            DiagnosticDescriptors.LengthOnNonString.Id,
                            location,
                            new EquatableArray<string>(new[] { "MinLength", property.Name, typeName })));
                        break;
                    }

                    if (attr.ConstructorArguments.Length >= 1)
                    {
                        (result ??= new List<DataAnnotationModel>()).Add(new DataAnnotationModel(
                            DataAnnotationKind.MinLength,
                            null, ConvertToDouble(attr.ConstructorArguments[0]), null,
                            new EquatableArray<string>(Array.Empty<string>()),
                        errorMessage));
                    }
                    break;

                case "MaxLengthAttribute":
                    if (!isString && !isCollection)
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            DiagnosticDescriptors.LengthOnNonString.Id,
                            location,
                            new EquatableArray<string>(new[] { "MaxLength", property.Name, typeName })));
                        break;
                    }

                    if (attr.ConstructorArguments.Length >= 1)
                    {
                        (result ??= new List<DataAnnotationModel>()).Add(new DataAnnotationModel(
                            DataAnnotationKind.MaxLength,
                            null, ConvertToDouble(attr.ConstructorArguments[0]), null,
                            new EquatableArray<string>(Array.Empty<string>()),
                        errorMessage));
                    }
                    break;

                case "RegularExpressionAttribute":
                    if (attr.ConstructorArguments.Length >= 1 &&
                        attr.ConstructorArguments[0].Value is string pattern)
                    {
                        // Validate the regex at build time so the user gets an
                        // immediate CB0008 instead of a runtime exception.
                        try
                        {
                            _ = new System.Text.RegularExpressions.Regex(pattern);
                        }
                        catch (ArgumentException ex)
                        {
                            diagnostics.Add(new DiagnosticInfo(
                                DiagnosticDescriptors.InvalidRegexPattern.Id,
                                location,
                                new EquatableArray<string>(new[] { property.Name, ex.Message })));
                            break;
                        }

                        (result ??= new List<DataAnnotationModel>()).Add(new DataAnnotationModel(
                            DataAnnotationKind.RegularExpression,
                            pattern, null, null,
                            new EquatableArray<string>(Array.Empty<string>()),
                        errorMessage));
                    }
                    break;

                case "UrlAttribute":
                    (result ??= new List<DataAnnotationModel>()).Add(new DataAnnotationModel(
                        DataAnnotationKind.Url,
                        null, null, null,
                        new EquatableArray<string>(Array.Empty<string>()),
                        errorMessage));
                    break;

                case "EmailAddressAttribute":
                    (result ??= new List<DataAnnotationModel>()).Add(new DataAnnotationModel(
                        DataAnnotationKind.EmailAddress,
                        null, null, null,
                        new EquatableArray<string>(Array.Empty<string>()),
                        errorMessage));
                    break;

                case "AllowedValuesAttribute":
                    (result ??= new List<DataAnnotationModel>()).Add(new DataAnnotationModel(
                        DataAnnotationKind.AllowedValues,
                        null, null, null,
                        ExtractValuesArg(attr),
                        errorMessage));
                    break;

                case "DeniedValuesAttribute":
                    (result ??= new List<DataAnnotationModel>()).Add(new DataAnnotationModel(
                        DataAnnotationKind.DeniedValues,
                        null, null, null,
                        ExtractValuesArg(attr),
                        errorMessage));
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Converts a Roslyn <see cref="TypedConstant"/> to a nullable double.
    /// Returns null when the constant is not a numeric type.
    /// </summary>
    private static double? ConvertToDouble(TypedConstant constant)
    {
        return constant.Value switch
        {
            int i => i,
            long l => l,
            double d => d,
            float f => f,
            decimal m => (double)m,
            byte b => b,
            sbyte sb => sb,
            short sh => sh,
            ushort us => us,
            uint ui => ui,
            ulong ul => ul,
            _ => null,
        };
    }

    /// <summary>
    /// Reads the <c>params object[]</c> constructor argument from
    /// <c>[AllowedValues]</c> or <c>[DeniedValues]</c> and converts each
    /// element to its <see cref="string"/> representation for embedding in
    /// the generated C# literal array.
    /// </summary>
    private static EquatableArray<string> ExtractValuesArg(AttributeData attr)
    {
        if (attr.ConstructorArguments.Length == 0)
        {
            return new EquatableArray<string>(Array.Empty<string>());
        }

        var arg = attr.ConstructorArguments[0];
        // params arguments appear as an array TypedConstant.
        if (arg.Kind == TypedConstantKind.Array)
        {
            var values = new string[arg.Values.Length];
            for (int i = 0; i < arg.Values.Length; i++)
            {
                values[i] = arg.Values[i].Value?.ToString() ?? "null";
            }

            return new EquatableArray<string>(values);
        }

        // Single non-array argument (unlikely, but defensive).
        return new EquatableArray<string>(new[] { arg.Value?.ToString() ?? "null" });
    }
}

/// <summary>
/// Lightweight return value from <c>ModelBuilder.ClassifyBinding</c>. A struct
/// rather than a record so we don't allocate one per property during the
/// hot incremental rebuild path.
/// </summary>
internal readonly record struct BindingClassification(
    BindingStrategy Strategy,
    bool IsNullableValueType,
    string? NestedTypeFullyQualifiedName,
    string? EnumFullyQualifiedName,
    string? ParseTypeKeyword,
    BindingStrategy CollectionElementStrategy,
    string? CollectionElementKeyword,
    bool IsCollectionArray);
