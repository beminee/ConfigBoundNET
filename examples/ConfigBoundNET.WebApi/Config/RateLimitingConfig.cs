// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System.ComponentModel.DataAnnotations;

namespace ConfigBoundNET.WebApi.Config;

/// <summary>
/// Rate-limiting configuration demonstrating:
/// <list type="bullet">
///   <item><c>[Range]</c> on numeric constraints.</item>
///   <item>Custom validation: burst size must not exceed requests-per-minute.</item>
///   <item>A nullable string property (<c>WhitelistedIps</c>) that is optional.</item>
/// </list>
/// </summary>
[ConfigSection("RateLimiting")]
public partial record RateLimitingConfig
{
    [Range(1, 10000)]
    public int RequestsPerMinute { get; init; } = 120;

    [Range(1, 1000)]
    public int BurstSize { get; init; } = 30;

    /// <summary>
    /// Comma-separated list of IPs that bypass rate limiting.
    /// Nullable — when absent, no IPs are whitelisted.
    /// </summary>
    public string? WhitelistedIps { get; init; }

    partial void ValidateCustom(List<string> failures)
    {
        if (BurstSize > RequestsPerMinute)
        {
            failures.Add(
                $"[{SectionName}] BurstSize ({BurstSize}) cannot exceed " +
                $"RequestsPerMinute ({RequestsPerMinute}).");
        }
    }
}
