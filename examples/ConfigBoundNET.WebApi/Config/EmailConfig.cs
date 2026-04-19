// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System.ComponentModel.DataAnnotations;

namespace ConfigBoundNET.WebApi.Config;

/// <summary>
/// SMTP email configuration demonstrating:
/// <list type="bullet">
///   <item><c>[Range]</c> on port numbers.</item>
///   <item><c>[EmailAddress]</c> on the sender address.</item>
///   <item><c>[StringLength]</c> on the display name.</item>
///   <item>Custom validation: credentials required when TLS is enabled.</item>
/// </list>
/// </summary>
[ConfigSection("Email")]
public partial record EmailConfig
{
    public string SmtpHost { get; init; } = default!;

    [Range(1, 65535)]
    public int SmtpPort { get; init; } = 587;

    [EmailAddress]
    public string SenderAddress { get; init; } = default!;

    [StringLength(100, MinimumLength = 1)]
    public string SenderDisplayName { get; init; } = default!;

    public bool UseTls { get; init; } = true;

    public string? Username { get; init; }

    // [Sensitive] — SMTP password. Never logged, never serialized as-is.
    [Sensitive]
    public string? Password { get; init; }

    partial void ValidateCustom(List<string> failures)
    {
        if (UseTls && string.IsNullOrWhiteSpace(Username))
        {
            failures.Add($"[{SectionName}] Username is required when UseTls is true.");
        }

        if (Username is not null && Password is null)
        {
            failures.Add($"[{SectionName}] Password is required when Username is set.");
        }
    }
}
