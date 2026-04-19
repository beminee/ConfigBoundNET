// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System.ComponentModel.DataAnnotations;
using ConfigBoundNET;

namespace ConfigBoundNET.Benchmarks.Configs;

/// <summary>
/// ConfigBoundNET-annotated benchmark target. Shape-identical to
/// <see cref="MicrosoftAppConfig"/> so <c>BindingBenchmarks</c> and
/// <c>ValidationBenchmarks</c> measure machinery cost, not workload cost.
/// </summary>
/// <remarks>
/// Every binding strategy ConfigBoundNET supports is exercised at least once:
/// scalars (string, int, TimeSpan, Uri, enum), nested <c>[ConfigSection]</c>,
/// scalar list, and string-keyed dictionary. Attribute coverage pulls in
/// <c>[Range]</c>, <c>[Url]</c>, <c>[EmailAddress]</c>, and
/// <c>[StringLength]</c> — a representative cross-section of the validation
/// paths.
/// </remarks>
[ConfigSection("App")]
public partial record ConfigBoundAppConfig
{
    public string ApiKey { get; init; } = default!;

    [Range(1, 100)]
    public int MaxRetries { get; init; }

    public TimeSpan Timeout { get; init; }

    [Url]
    public string Endpoint { get; init; } = default!;

    [EmailAddress]
    public string Email { get; init; } = default!;

    [StringLength(100, MinimumLength = 3)]
    public string Name { get; init; } = default!;

    public ConfigBoundLogLevel Level { get; init; }

    public ConfigBoundRetryConfig Retry { get; init; } = default!;

    public List<string> Hosts { get; init; } = new();

    public Dictionary<string, string> Tags { get; init; } = new();
}

/// <summary>Nested <c>[ConfigSection]</c> so the benchmark exercises the recursive constructor path.</summary>
[ConfigSection("__retry__")]
public partial record ConfigBoundRetryConfig
{
    [Range(1, 20)]
    public int MaxAttempts { get; init; }

    public TimeSpan Backoff { get; init; }
}

/// <summary>Enum parsed via <c>Enum.TryParse</c> — the AOT-safe path ConfigBoundNET emits.</summary>
public enum ConfigBoundLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
}
