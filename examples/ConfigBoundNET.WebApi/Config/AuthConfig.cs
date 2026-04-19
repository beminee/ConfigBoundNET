// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System.ComponentModel.DataAnnotations;

namespace ConfigBoundNET.WebApi.Config;

/// <summary>
/// JWT authentication configuration demonstrating:
/// <list type="bullet">
///   <item><c>[MinLength]</c> on the secret key (must be &gt;= 32 chars for HMAC-SHA256).</item>
///   <item><c>[Url]</c> validation on issuer / audience.</item>
///   <item><c>[Range]</c> on token lifetimes.</item>
/// </list>
/// </summary>
[ConfigSection("Auth")]
public partial record AuthConfig
{
    // [Sensitive] — HMAC signing key; leaking it into logs breaks every
    // token the API has ever issued.
    [Sensitive]
    [MinLength(32)]
    public string JwtSecret { get; init; } = default!;

    [Url]
    public string Issuer { get; init; } = default!;

    [Url]
    public string Audience { get; init; } = default!;

    [Range(1, 1440)]
    public int TokenLifetimeMinutes { get; init; } = 60;

    [Range(1, 365)]
    public int RefreshTokenLifetimeDays { get; init; } = 30;
}
