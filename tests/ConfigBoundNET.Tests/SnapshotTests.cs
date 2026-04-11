// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using VerifyTests;
using Xunit;

namespace ConfigBoundNET.Tests;

/// <summary>
/// Snapshot tests that pin the exact generated output for representative
/// scenarios. Any change to the emitter surfaces as a diff in the
/// <c>.verified.cs</c> files under the <c>Snapshots/</c> directory,
/// which must be explicitly accepted before the test passes again.
/// </summary>
public sealed class SnapshotTests
{
    static SnapshotTests()
    {
        VerifySourceGenerators.Initialize();
    }

    private static SettingsTask VerifyDriver(
        string source,
        [CallerFilePath] string sourceFile = "",
        [CallerMemberName] string member = "")
    {
        var driver = GeneratorHarness.CreateDriver(source);
        return Verifier.Verify(driver, sourceFile: sourceFile)
            .UseDirectory("Snapshots")
            .UseMethodName(member);
    }

    // ── Core generation ──────────────────────────────────────────────────

    [Fact]
    public Task Basic_record_with_string_and_int()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Db")]
            public partial record DbConfig
            {
                public string Conn { get; init; } = default!;
                public int CommandTimeoutSeconds { get; init; } = 30;
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task Parameterless_attribute_infers_section_name()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection]
            public partial record PaymentsConfig
            {
                public string ApiKey { get; init; } = default!;
                public decimal Amount { get; init; }
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task Class_instead_of_record()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Cache")]
            public partial class CacheConfig
            {
                public string Host { get; set; } = default!;
                public int Port { get; set; } = 6379;
                public bool UseSsl { get; set; }
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task Global_namespace_no_namespace_declaration()
    {
        const string Source = """
            using ConfigBoundNET;

            [ConfigSection("App")]
            public partial record AppConfig
            {
                public string Name { get; init; } = default!;
            }
            """;

        return VerifyDriver(Source);
    }

    // ── Property types ───────────────────────────────────────────────────

    [Fact]
    public Task All_primitive_types()
    {
        const string Source = """
            using ConfigBoundNET;
            using System;

            namespace MyApp;

            [ConfigSection("Types")]
            public partial record TypesConfig
            {
                public string Text { get; init; } = default!;
                public bool Enabled { get; init; }
                public byte SmallNum { get; init; }
                public int Count { get; init; }
                public long BigNum { get; init; }
                public double Ratio { get; init; }
                public decimal Price { get; init; }
                public Guid TenantId { get; init; }
                public TimeSpan Timeout { get; init; }
                public DateTime CreatedAt { get; init; }
                public DateTimeOffset DeployedAt { get; init; }
                public Uri Endpoint { get; init; } = default!;
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task Enum_property()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            public enum LogLevel { Trace, Debug, Info, Warn, Error }

            [ConfigSection("Log")]
            public partial record LogConfig
            {
                public LogLevel MinLevel { get; init; }
                public string? OutputPath { get; init; }
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task Nullable_and_optional_properties()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Opt")]
            public partial record OptionalConfig
            {
                public string Required { get; init; } = default!;
                public string? OptionalString { get; init; }
                public int? OptionalInt { get; init; }
                public bool? OptionalBool { get; init; }
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task Nested_config_type()
    {
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Db")]
            public partial record DbConfig
            {
                public string Conn { get; init; } = default!;
                public RetryPolicy Retry { get; init; } = default!;
            }

            [ConfigSection("__nested__")]
            public partial record RetryPolicy
            {
                public int MaxAttempts { get; init; } = 3;
                public bool Enabled { get; init; }
            }
            """;

        return VerifyDriver(Source);
    }

    // ── DataAnnotations ──────────────────────────────────────────────────

    [Fact]
    public Task Range_and_StringLength_annotations()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Api")]
            public partial record ApiConfig
            {
                [Range(1, 65535)]
                public int Port { get; init; } = 8080;

                [StringLength(200, MinimumLength = 5)]
                public string DisplayName { get; init; } = default!;
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task RegularExpression_annotation()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Web")]
            public partial record WebConfig
            {
                [RegularExpression(@"^https?://")]
                public string Endpoint { get; init; } = default!;
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task Url_and_EmailAddress_annotations()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Contact")]
            public partial record ContactConfig
            {
                [Url]
                public string Website { get; init; } = default!;

                [EmailAddress]
                public string AdminEmail { get; init; } = default!;
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task MinLength_and_MaxLength_annotations()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Auth")]
            public partial record AuthConfig
            {
                [MinLength(32)]
                public string Secret { get; init; } = default!;

                [MaxLength(100)]
                public string Issuer { get; init; } = default!;
            }
            """;

        return VerifyDriver(Source);
    }

    [Fact]
    public Task Multiple_annotations_on_same_property()
    {
        const string Source = """
            using ConfigBoundNET;
            using System.ComponentModel.DataAnnotations;

            namespace MyApp;

            [ConfigSection("Pwd")]
            public partial record PasswordConfig
            {
                [MinLength(8)]
                [MaxLength(128)]
                [RegularExpression(@"^(?=.*[A-Z])(?=.*[0-9])")]
                public string DefaultPassword { get; init; } = default!;
            }
            """;

        return VerifyDriver(Source);
    }

    // ── Custom validation hook ───────────────────────────────────────────

    [Fact]
    public Task ValidateCustom_hook_is_emitted()
    {
        // The hook declaration should appear even when no properties exist.
        const string Source = """
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("Empty")]
            public partial record EmptyConfig;
            """;

        return VerifyDriver(Source);
    }
}
