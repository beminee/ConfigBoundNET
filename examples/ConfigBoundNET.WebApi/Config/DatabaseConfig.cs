// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System.ComponentModel.DataAnnotations;

namespace ConfigBoundNET.WebApi.Config;

/// <summary>
/// Nested retry-policy configuration used by <see cref="DatabaseConfig"/>.
/// Must be annotated with <c>[ConfigSection]</c> so the generator emits the
/// recursive <c>(IConfigurationSection)</c> constructor that the outer type
/// calls into.
/// </summary>
[ConfigSection("__nested__")]
public partial record RetryConfig
{
    [Range(1, 20)]
    public int MaxAttempts { get; init; } = 3;

    [Range(1, 60)]
    public int BackoffSeconds { get; init; } = 2;
}

/// <summary>
/// Database configuration demonstrating:
/// <list type="bullet">
///   <item>Required string (<c>ConnectionString</c>).</item>
///   <item><c>[Range]</c> on numeric types.</item>
///   <item>Nested <c>[ConfigSection]</c> type (<see cref="Retry"/>).</item>
///   <item>Custom cross-field validation via <c>ValidateCustom</c>.</item>
/// </list>
/// </summary>
[ConfigSection("Database")]
public partial record DatabaseConfig
{
    public string ConnectionString { get; init; } = default!;

    [Range(1, 300)]
    public int CommandTimeoutSeconds { get; init; } = 30;

    [Range(1, 1000)]
    public int MaxPoolSize { get; init; } = 100;

    public bool EnableRetry { get; init; }

    public RetryConfig Retry { get; init; } = default!;

    /// <summary>
    /// If retries are enabled, the retry policy must actually be configured.
    /// </summary>
    partial void ValidateCustom(List<string> failures)
    {
        if (EnableRetry && Retry is null)
        {
            failures.Add($"[{SectionName}] EnableRetry is true but the Retry section is missing.");
        }
    }
}
