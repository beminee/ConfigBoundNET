// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System;

namespace ConfigBoundNET;

/// <summary>
/// Classifies which DataAnnotations attribute was found on a config property.
/// Each value maps one-to-one to a branch inside
/// <see cref="SourceEmitter"/>'s <c>WriteAnnotationCheck</c>.
/// </summary>
internal enum DataAnnotationKind
{
    /// <summary><c>[Required]</c> (may be redundant with nullability; CB0009 warns).</summary>
    Required,

    /// <summary><c>[Range(min, max)]</c> on a numeric property.</summary>
    Range,

    /// <summary><c>[StringLength(max, MinimumLength = n)]</c> on a string property.</summary>
    StringLength,

    /// <summary><c>[MinLength(n)]</c> on a string property.</summary>
    MinLength,

    /// <summary><c>[MaxLength(n)]</c> on a string property.</summary>
    MaxLength,

    /// <summary><c>[RegularExpression(pattern)]</c> on a string property.</summary>
    RegularExpression,

    /// <summary><c>[Url]</c> on a string property.</summary>
    Url,

    /// <summary><c>[EmailAddress]</c> on a string property.</summary>
    EmailAddress,

    /// <summary><c>[AllowedValues(...)]</c> (.NET 8+) on any property.</summary>
    AllowedValues,

    /// <summary><c>[DeniedValues(...)]</c> (.NET 8+) on any property.</summary>
    DeniedValues,
}

/// <summary>
/// Flat, value-equatable representation of a single DataAnnotations attribute
/// found on a config property. Captured during model building and consumed by
/// the emitter to produce explicit, reflection-free validation checks.
/// </summary>
/// <remarks>
/// <para>
/// A single record with nullable fields covers all ten attribute shapes without
/// polymorphism. All fields are primitives, strings, or
/// <see cref="EquatableArray{T}"/>, so the type satisfies the incremental
/// caching equality requirement.
/// </para>
/// <para>
/// <see cref="NumericArg1"/> and <see cref="NumericArg2"/> use <c>double</c>
/// because <c>[Range]</c> accepts <c>int</c> or <c>double</c> constructors
/// and <c>double</c> can represent both without loss for the typical config
/// value range. The emitter casts back to the property's actual type using
/// <see cref="ConfigPropertyModel.ParseTypeKeyword"/> when generating the
/// comparison literal.
/// </para>
/// </remarks>
/// <param name="Kind">Which DataAnnotations attribute this represents.</param>
/// <param name="StringArg1">
/// The regex pattern for <see cref="DataAnnotationKind.RegularExpression"/>.
/// Null for all other kinds.
/// </param>
/// <param name="NumericArg1">
/// <list type="bullet">
///   <item><description><see cref="DataAnnotationKind.Range"/>: the minimum value.</description></item>
///   <item><description><see cref="DataAnnotationKind.StringLength"/>: the maximum length.</description></item>
///   <item><description><see cref="DataAnnotationKind.MinLength"/>: the minimum length.</description></item>
///   <item><description><see cref="DataAnnotationKind.MaxLength"/>: the maximum length.</description></item>
/// </list>
/// Null for all other kinds.
/// </param>
/// <param name="NumericArg2">
/// <list type="bullet">
///   <item><description><see cref="DataAnnotationKind.Range"/>: the maximum value.</description></item>
///   <item><description><see cref="DataAnnotationKind.StringLength"/>: the <c>MinimumLength</c> named argument.</description></item>
/// </list>
/// Null for all other kinds.
/// </param>
/// <param name="ValuesArg">
/// The set of allowed or denied values for <see cref="DataAnnotationKind.AllowedValues"/>
/// and <see cref="DataAnnotationKind.DeniedValues"/>, stored as C# literal strings.
/// Empty for all other kinds.
/// </param>
/// <param name="ErrorMessage">
/// Optional custom error message from the attribute's <c>ErrorMessage</c> named
/// argument. Supports <c>{0}</c> (property display name), <c>{1}</c> (first
/// numeric arg), and <c>{2}</c> (second numeric arg) format placeholders,
/// matching the behaviour of <c>System.ComponentModel.DataAnnotations</c>
/// attributes. Null when the attribute doesn't specify a custom message;
/// the emitter then uses its built-in default.
/// </param>
internal sealed record DataAnnotationModel(
    DataAnnotationKind Kind,
    string? StringArg1,
    double? NumericArg1,
    double? NumericArg2,
    EquatableArray<string> ValuesArg,
    string? ErrorMessage) : IEquatable<DataAnnotationModel>;
