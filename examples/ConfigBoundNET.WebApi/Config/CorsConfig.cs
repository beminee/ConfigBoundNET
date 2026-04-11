// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System.ComponentModel.DataAnnotations;

namespace ConfigBoundNET.WebApi.Config;

/// <summary>
/// CORS (Cross-Origin Resource Sharing) configuration demonstrating collection
/// support with all three collection types:
/// <list type="bullet">
///   <item><c>List&lt;string&gt;</c> for allowed origins.</item>
///   <item><c>string[]</c> for allowed methods.</item>
///   <item><c>Dictionary&lt;string, string&gt;</c> for custom response headers.</item>
/// </list>
/// Also demonstrates <c>[MinLength]</c> on collections (must have at least
/// one origin) and a custom cross-field validation hook.
/// </summary>
[ConfigSection("Cors")]
public partial record CorsConfig
{
    /// <summary>
    /// Origins allowed to make cross-origin requests.
    /// Must contain at least one entry.
    /// </summary>
    /// <example><c>["https://app.example.com", "https://admin.example.com"]</c></example>
    [MinLength(1)]
    public List<string> AllowedOrigins { get; init; } = new();

    /// <summary>
    /// HTTP methods allowed in cross-origin requests.
    /// Defaults to <c>["GET"]</c> if absent from config.
    /// </summary>
    /// <example><c>["GET", "POST", "PUT", "DELETE"]</c></example>
    public string[] AllowedMethods { get; init; } = ["GET"];

    /// <summary>
    /// Headers the client is allowed to send in cross-origin requests.
    /// </summary>
    public string[] AllowedHeaders { get; init; } = ["Content-Type", "Authorization"];

    /// <summary>
    /// Additional response headers exposed to the browser. Bound from a
    /// JSON object where keys are header names and values are header values.
    /// </summary>
    /// <example><c>{ "X-Request-Id": "*", "X-RateLimit-Remaining": "*" }</c></example>
    public Dictionary<string, string> ExposedHeaders { get; init; } = new();

    /// <summary>
    /// Whether the browser should include credentials (cookies, auth headers)
    /// in cross-origin requests.
    /// </summary>
    public bool AllowCredentials { get; init; }

    /// <summary>
    /// How long (in seconds) the browser should cache the preflight response.
    /// </summary>
    [Range(0, 86400)]
    public int PreflightMaxAgeSeconds { get; init; } = 600;

    /// <summary>
    /// Cross-field rule: <c>AllowCredentials</c> + wildcard origin is a
    /// browser-rejected combination (the spec forbids <c>Access-Control-Allow-Origin: *</c>
    /// when credentials are included).
    /// </summary>
    partial void ValidateCustom(List<string> failures)
    {
        if (AllowCredentials && AllowedOrigins.Contains("*"))
        {
            failures.Add(
                $"[{SectionName}] AllowCredentials is true but AllowedOrigins contains '*'. " +
                "Browsers reject this combination. Use explicit origins instead.");
        }
    }
}
