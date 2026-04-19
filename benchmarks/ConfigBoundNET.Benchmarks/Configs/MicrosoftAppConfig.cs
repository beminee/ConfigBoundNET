// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace ConfigBoundNET.Benchmarks.Configs;

/// <summary>
/// Microsoft-path benchmark target: plain POCO, same shape as
/// <see cref="ConfigBoundAppConfig"/>. Bound via
/// <c>section.Get&lt;MicrosoftAppConfig&gt;()</c>, which the
/// <c>EnableConfigurationBindingGenerator</c> MSBuild flag intercepts and
/// rewires to a generated AOT-friendly binder.
/// </summary>
/// <remarks>
/// Deliberately a <c>class</c> with public setters (not a record): Microsoft's
/// binding generator wants parameterless construction + property setters, and
/// records with init-only properties work too but add variance we don't want
/// in the comparison. The equivalent type for ConfigBoundNET
/// (<see cref="ConfigBoundAppConfig"/>) uses the idiomatic
/// <c>partial record</c> + <c>init</c> form; the shape match is on property
/// types, not on type kind.
/// </remarks>
public sealed class MicrosoftAppConfig
{
    [Required]
    public string ApiKey { get; set; } = default!;

    [Range(1, 100)]
    public int MaxRetries { get; set; }

    public TimeSpan Timeout { get; set; }

    [Required]
    [Url]
    public string Endpoint { get; set; } = default!;

    [Required]
    [EmailAddress]
    public string Email { get; set; } = default!;

    [Required]
    [StringLength(100, MinimumLength = 3)]
    public string Name { get; set; } = default!;

    public MicrosoftLogLevel Level { get; set; }

    // [ValidateObjectMembers] is the Microsoft-generator equivalent of
    // ConfigBoundNET's automatic nested validation. Without it, the
    // OptionsValidator generator emits SYSLIB1212 and the nested type's
    // annotations would be silently ignored.
    [ValidateObjectMembers]
    public MicrosoftRetryConfig Retry { get; set; } = new();

    public List<string> Hosts { get; set; } = new();

    public Dictionary<string, string> Tags { get; set; } = new();
}

/// <summary>Parallel nested type for the Microsoft path.</summary>
public sealed class MicrosoftRetryConfig
{
    [Range(1, 20)]
    public int MaxAttempts { get; set; }

    public TimeSpan Backoff { get; set; }
}

/// <summary>Parallel enum for the Microsoft path.</summary>
public enum MicrosoftLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
}
