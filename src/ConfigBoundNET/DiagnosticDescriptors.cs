// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using Microsoft.CodeAnalysis;

namespace ConfigBoundNET;

/// <summary>
/// Central registry of every diagnostic raised by the ConfigBoundNET generator.
/// </summary>
/// <remarks>
/// IDs live in the <c>CB0xxx</c> range. They are stable public contract —
/// downstream users may reference them from <c>NoWarn</c> or editorconfig, so
/// do not repurpose an existing ID; add a new one instead.
/// </remarks>
internal static class DiagnosticDescriptors
{
    /// <summary>The category string attached to every diagnostic in this file.</summary>
    private const string Category = "ConfigBoundNET";

    /// <summary>
    /// CB0001 — the annotated type is missing the <c>partial</c> modifier.
    /// The generator cannot extend a non-partial type, so it bails out early.
    /// </summary>
    public static readonly DiagnosticDescriptor MustBePartial = new(
        id: "CB0001",
        title: "Config type must be declared partial",
        messageFormat: "'{0}' is decorated with [ConfigSection] but is not declared partial. Add the 'partial' modifier so ConfigBoundNET can extend it with the generated validator.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The ConfigBoundNET generator extends annotated types with a nested Validator and helper members. This requires the 'partial' modifier.");

    /// <summary>
    /// CB0002 — a <c>[ConfigSection("")]</c> value was null, empty, or whitespace.
    /// </summary>
    public static readonly DiagnosticDescriptor EmptySectionName = new(
        id: "CB0002",
        title: "Configuration section name cannot be empty",
        messageFormat: "[ConfigSection] on '{0}' requires a non-empty section name. Provide the name of the configuration section to bind, e.g. [ConfigSection(\"Db\")].",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A section name is required so the generator knows which IConfiguration path to bind against.");

    /// <summary>
    /// CB0003 — the annotated type is a nested type. Nested types complicate
    /// partial-type extension because the containing type must also be partial at
    /// every level. We deliberately reject this to keep the emitter simple.
    /// </summary>
    public static readonly DiagnosticDescriptor NestedTypeNotSupported = new(
        id: "CB0003",
        title: "Nested config types are not supported",
        messageFormat: "'{0}' is a nested type. [ConfigSection] can only be applied to top-level types. Move the type out of its containing type.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// CB0004 — the annotated type exposes no writable properties, so the
    /// generator has nothing to bind or validate.
    /// </summary>
    public static readonly DiagnosticDescriptor NoBindableMembers = new(
        id: "CB0004",
        title: "Config type has no bindable properties",
        messageFormat: "'{0}' has no public writable or init-only properties so ConfigBoundNET has nothing to bind from configuration",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// CB0005 — the attribute target is neither a class nor a record.
    /// Structs are technically bindable but struct options types are an anti-pattern
    /// in the Options framework, so we reject them here.
    /// </summary>
    public static readonly DiagnosticDescriptor InvalidTargetKind = new(
        id: "CB0005",
        title: "Unsupported target for [ConfigSection]",
        messageFormat: "'{0}' is not a class or record. [ConfigSection] can only be applied to class or record types (structs are not supported).",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// CB0010 — the property type is outside the set of types ConfigBoundNET
    /// can bind without reflection. The generator skips the property and the user
    /// gets a default-constructed value at runtime.
    /// </summary>
    /// <remarks>
    /// This warning is the price of the AOT-friendly emitter. The supported types
    /// are documented next to <see cref="BindingStrategy"/> — everything
    /// else needs either a wrapper type, a custom <c>IValidateOptions&lt;T&gt;</c>,
    /// or a manual <c>services.Configure&lt;T&gt;()</c> call layered on top.
    /// </remarks>
    /// <summary>
    /// CB0006 — <c>[Range]</c> is applied to a property whose type is not
    /// numeric (<see cref="BindingStrategy.Integer"/> or
    /// <see cref="BindingStrategy.FloatingPoint"/>). The annotation is ignored.
    /// </summary>
    public static readonly DiagnosticDescriptor RangeOnNonNumeric = new(
        id: "CB0006",
        title: "[Range] applied to non-numeric property",
        messageFormat: "[Range] on property '{0}' is invalid because type '{1}' is not numeric and will be ignored",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// CB0007 — a length-related attribute (<c>[StringLength]</c>,
    /// <c>[MinLength]</c>, or <c>[MaxLength]</c>) is applied to a non-string
    /// property. The annotation is ignored.
    /// </summary>
    public static readonly DiagnosticDescriptor LengthOnNonString = new(
        id: "CB0007",
        title: "Length attribute applied to non-string property",
        messageFormat: "[{0}] on property '{1}' is invalid because type '{2}' is not a string and will be ignored",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// CB0008 — the pattern supplied to <c>[RegularExpression]</c> is not a
    /// valid .NET regex. Validated at build time via <c>new Regex(pattern)</c>
    /// inside the generator.
    /// </summary>
    public static readonly DiagnosticDescriptor InvalidRegexPattern = new(
        id: "CB0008",
        title: "[RegularExpression] pattern is not a valid regex",
        messageFormat: "[RegularExpression] on property '{0}' has an invalid pattern: {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// CB0009 — <c>[Required]</c> is applied to a property that is already
    /// non-nullable. The generator validates non-nullable properties
    /// automatically, so the attribute is redundant. Informational only.
    /// </summary>
    public static readonly DiagnosticDescriptor RedundantRequired = new(
        id: "CB0009",
        title: "[Required] is redundant on non-nullable property",
        messageFormat: "[Required] on property '{0}' is redundant because the property is already non-nullable and will be validated automatically",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedBindingType = new(
        id: "CB0010",
        title: "Property type is not bindable by ConfigBoundNET",
        messageFormat: "Property '{0}' has type '{1}' which ConfigBoundNET cannot bind without reflection and will be left at its default value",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ConfigBoundNET emits an explicit, AOT-safe binder. Properties whose types fall outside the supported set (string, primitives, Guid, TimeSpan, DateTime(Offset), Uri, enums, nested [ConfigSection] types, list-like collections of any of those, and Dictionary<string, T> where T is one of those) cannot be bound and are silently skipped.");

    /// <summary>
    /// CB0011 — a property refers to a nested <c>[ConfigSection]</c> type
    /// (directly or as a collection element / dictionary value) whose
    /// definition lives in another assembly that the current compilation
    /// has not analyzed. The JSON-schema emitter cannot expand it inline
    /// and falls back to <c>{"type":"object","additionalProperties":true}</c>,
    /// which is permissive but still valid schema. Informational only —
    /// the runtime binder is unaffected because nested
    /// <c>[ConfigSection]</c> types carry their own generated constructor
    /// and validator in their own assembly.
    /// </summary>
    public static readonly DiagnosticDescriptor ExternalNestedConfigNotAnalyzed = new(
        id: "CB0011",
        title: "Nested [ConfigSection] type defined outside this compilation",
        messageFormat: "Nested [ConfigSection] type '{0}' referenced by '{1}' is defined in another assembly; the emitted JSON schema for this property falls back to a permissive object shape",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "The JSON-schema aggregate output can only inline sub-schemas for [ConfigSection] types visible to the current compilation. Cross-assembly nested configs fall back to '{\"type\":\"object\",\"additionalProperties\":true}'. Runtime binding and validation are unaffected.");
}
