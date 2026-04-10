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
/// <param name="Binding">
/// How the AOT-safe emitted binder should read this property out of an
/// <c>IConfigurationSection</c>. Drives <c>SourceEmitter.WriteBindMethod</c>.
/// </param>
/// <param name="IsNullableValueType">
/// <see langword="true"/> when the property is a <c>Nullable&lt;T&gt;</c>. Used by the
/// emitter so it can write <c>null</c> back when the configuration key is absent.
/// </param>
/// <param name="NestedTypeFullyQualifiedName">
/// For <see cref="BindingStrategy.NestedConfig"/>, the fully qualified name of the
/// nested config type (which must itself be <c>[ConfigSection]</c>-annotated and will
/// therefore have its own generated <c>Bind</c> method to call into).
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
internal sealed record ConfigPropertyModel(
    string Name,
    bool IsRequired,
    bool IsReferenceType,
    bool IsString,
    BindingStrategy Binding,
    bool IsNullableValueType,
    string? NestedTypeFullyQualifiedName,
    string? EnumFullyQualifiedName,
    string? ParseTypeKeyword) : IEquatable<ConfigPropertyModel>;

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
        "CB0010" => DiagnosticDescriptors.UnsupportedBindingType,
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

            properties.Add(new ConfigPropertyModel(
                Name: property.Name,
                IsRequired: isRequired,
                IsReferenceType: isReferenceType,
                IsString: isString,
                Binding: classification.Strategy,
                IsNullableValueType: classification.IsNullableValueType,
                NestedTypeFullyQualifiedName: classification.NestedTypeFullyQualifiedName,
                EnumFullyQualifiedName: classification.EnumFullyQualifiedName,
                ParseTypeKeyword: classification.ParseTypeKeyword));
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
                return new BindingClassification(BindingStrategy.String, false, null, null, null);

            case SpecialType.System_Boolean:
                return new BindingClassification(BindingStrategy.Boolean, isNullableValueType, null, null, "bool");

            case SpecialType.System_Byte:
                return new BindingClassification(BindingStrategy.Integer, isNullableValueType, null, null, "byte");
            case SpecialType.System_SByte:
                return new BindingClassification(BindingStrategy.Integer, isNullableValueType, null, null, "sbyte");
            case SpecialType.System_Int16:
                return new BindingClassification(BindingStrategy.Integer, isNullableValueType, null, null, "short");
            case SpecialType.System_UInt16:
                return new BindingClassification(BindingStrategy.Integer, isNullableValueType, null, null, "ushort");
            case SpecialType.System_Int32:
                return new BindingClassification(BindingStrategy.Integer, isNullableValueType, null, null, "int");
            case SpecialType.System_UInt32:
                return new BindingClassification(BindingStrategy.Integer, isNullableValueType, null, null, "uint");
            case SpecialType.System_Int64:
                return new BindingClassification(BindingStrategy.Integer, isNullableValueType, null, null, "long");
            case SpecialType.System_UInt64:
                return new BindingClassification(BindingStrategy.Integer, isNullableValueType, null, null, "ulong");

            case SpecialType.System_Single:
                return new BindingClassification(BindingStrategy.FloatingPoint, isNullableValueType, null, null, "float");
            case SpecialType.System_Double:
                return new BindingClassification(BindingStrategy.FloatingPoint, isNullableValueType, null, null, "double");
            case SpecialType.System_Decimal:
                return new BindingClassification(BindingStrategy.FloatingPoint, isNullableValueType, null, null, "decimal");
        }

        // ── Enums (matched before the named-type sniff so System.Enum subtypes win). ──
        if (type.TypeKind == TypeKind.Enum)
        {
            return new BindingClassification(
                BindingStrategy.Enum,
                isNullableValueType,
                NestedTypeFullyQualifiedName: null,
                EnumFullyQualifiedName: type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                ParseTypeKeyword: null);
        }

        // ── Named non-special types: Guid, TimeSpan, DateTime, Uri, nested config. ──
        var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        switch (fullName)
        {
            case "global::System.Guid":
                return new BindingClassification(BindingStrategy.Guid, isNullableValueType, null, null, null);

            case "global::System.TimeSpan":
                return new BindingClassification(BindingStrategy.TimeSpan, isNullableValueType, null, null, null);

            case "global::System.DateTime":
                return new BindingClassification(BindingStrategy.DateTime, isNullableValueType, null, null, null);

            case "global::System.DateTimeOffset":
                return new BindingClassification(BindingStrategy.DateTimeOffset, isNullableValueType, null, null, null);

            case "global::System.Uri":
                // Uri is a reference type, so the "nullable" form is just `Uri?`,
                // not Nullable<Uri>. isNullableValueType therefore stays false.
                return new BindingClassification(BindingStrategy.Uri, false, null, null, null);
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
                ParseTypeKeyword: null);
        }

        return new BindingClassification(BindingStrategy.Unsupported, false, null, null, null);
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
    string? ParseTypeKeyword);
