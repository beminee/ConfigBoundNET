// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace ConfigBoundNET.Tests;

/// <summary>
/// Runtime tests for the <c>validateOnStart</c> parameter on the
/// generator-emitted <c>AddConfigBoundSections</c> aggregate extension.
/// Snapshot coverage (in <c>SnapshotTests</c>) pins the shape of the emitted
/// code; these tests pin the observable semantics — specifically that
/// <c>true</c> moves validation to host-start while <c>false</c> keeps the
/// v2.x lazy-validation behaviour.
/// </summary>
/// <remarks>
/// Rather than spin up a full <see cref="IHost"/>, each test builds a plain
/// <see cref="ServiceProvider"/> from the generated registrations and then
/// manually iterates <c>sp.GetServices&lt;IHostedService&gt;()</c>,
/// mirroring what <c>Host.StartAsync</c> would do internally. That's enough
/// to exercise the <c>ValidationHostedService</c> that
/// <c>ValidateOnStart()</c> registers, without pulling the full host
/// lifecycle (logging, signal handlers, etc.) into the test cost.
/// </remarks>
public sealed class AggregateValidateOnStartTests
{
    /// <summary>
    /// Fixture used by both tests: a single <c>[ConfigSection("Db")]</c>
    /// record with one required (non-nullable) string property. Making
    /// <c>Conn</c> whitespace-only in the bound config triggers the
    /// generator's <c>IsNullOrWhiteSpace</c> validator — the simplest way
    /// to force an <see cref="OptionsValidationException"/> with a
    /// deterministic message substring we can assert against.
    /// </summary>
    private const string FixtureSource = """
        using ConfigBoundNET;

        namespace MyApp;

        [ConfigSection("Db")]
        public partial record DbConfig
        {
            public string Conn { get; init; } = default!;
        }
        """;

    /// <summary>Configuration that will fail the Conn IsNullOrWhiteSpace check.</summary>
    private static Dictionary<string, string?> InvalidConfig() =>
        new() { ["Db:Conn"] = "   " };

    [Fact]
    public async Task Without_flag_validation_is_deferred_until_first_options_resolve()
    {
        // Arrange: compile + run the generator, register every [ConfigSection]
        // via AddConfigBoundSections with the default validateOnStart=false.
        var (assembly, services) = GeneratorHarness.CompileAndAggregate(
            FixtureSource,
            InvalidConfig(),
            validateOnStart: false);

        await using var sp = services.BuildServiceProvider();

        // Act: simulate host start. With validateOnStart=false, ValidateOnStart()
        // never ran, so no ValidationHostedService is registered — every hosted
        // service that IS registered (if any) must return cleanly. This models
        // host.StartAsync() without actually building an IHost.
        await SimulateHostStartAsync(sp);

        // Act: force a resolution of IOptions<DbConfig>.Value. This drives the
        // ConfigBoundOptionsFactory → generated constructor → IValidateOptions
        // pipeline, which detects the whitespace Conn and throws.
        var dbConfigType = assembly.GetTypes().Single(t => t.Name == "DbConfig");
        var closedOptionsType = typeof(IOptions<>).MakeGenericType(dbConfigType);
        var optionsInstance = sp.GetRequiredService(closedOptionsType);
        var valueProperty = closedOptionsType.GetProperty("Value")!;

        // Reflection wraps the real exception in TargetInvocationException.
        // Assert on the inner exception — that's the type the app would see
        // at runtime (the reflection wrapper is a test-harness artefact).
        var wrapped = Assert.Throws<TargetInvocationException>(
            () => valueProperty.GetValue(optionsInstance));
        var inner = Assert.IsType<OptionsValidationException>(wrapped.InnerException);
        Assert.Contains("[Db:Conn]", string.Join(" ", inner.Failures));
    }

    [Fact]
    public async Task With_flag_missing_top_level_section_fails_at_host_start()
    {
        // The fail-fast promise of validateOnStart:true only pays off when a
        // top-level [ConfigSection] type whose root section is entirely
        // absent still produces a startup failure. Before the aggregate
        // classification tightened, the .Exists() gate silently skipped
        // such types — hiding real misconfigurations until a cold-path
        // request. This test pins that gate-tightening: with an empty
        // config, the DbConfig top-level entry is registered unconditionally
        // and its validator fires during simulated host start.
        var emptyConfig = new Dictionary<string, string?>();
        var (_, services) = GeneratorHarness.CompileAndAggregate(
            FixtureSource,
            emptyConfig,
            validateOnStart: true);

        await using var sp = services.BuildServiceProvider();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(
            () => SimulateHostStartAsync(sp));

        Assert.Contains("[Db:Conn]", string.Join(" ", ex.Failures));
    }

    [Fact]
    public async Task With_flag_missing_nested_only_section_is_skipped_via_inference()
    {
        // Counterpart to the top-level test: EndpointConfig is referenced
        // as a List<EndpointConfig> property inside DbConfig, so the
        // aggregate classification infers it as nested and applies the
        // .Exists() gate. The Db root section is present (with an empty
        // Endpoints array), the __endpoint__ section is absent — and the
        // aggregate must NOT fail startup on the missing nested-only
        // section because that section name is a throwaway scaffold the
        // user never intends to author at root.
        const string NestedFixture = """
            using System.Collections.Generic;
            using ConfigBoundNET;

            namespace MyApp;

            [ConfigSection("__endpoint__")]
            public partial record EndpointConfig
            {
                public string Url { get; init; } = default!;
            }

            [ConfigSection("Db")]
            public partial record DbConfig
            {
                public string Conn { get; init; } = default!;
                public List<EndpointConfig> Endpoints { get; init; } = new();
            }
            """;

        var validConfig = new Dictionary<string, string?>
        {
            ["Db:Conn"] = "Server=localhost;Database=X;",
        };

        var (_, services) = GeneratorHarness.CompileAndAggregate(
            NestedFixture,
            validConfig,
            validateOnStart: true);

        await using var sp = services.BuildServiceProvider();

        // Host start must succeed: Db validates cleanly and EndpointConfig
        // was gated away because inference classified it nested.
        await SimulateHostStartAsync(sp);
    }

    [Fact]
    public async Task With_flag_missing_nested_only_section_is_skipped_via_attribute_flag()
    {
        // Cross-assembly scenario: the library author ships an
        // IsNestedOnly=true type whose nested usage lives in a consumer's
        // assembly (not in the generator's current compilation). In-
        // compilation inference would classify it top-level — but the
        // explicit flag must override and keep the gate applied.
        const string LibraryFixture = """
            using ConfigBoundNET;

            namespace Lib;

            [ConfigSection("__endpoint__", IsNestedOnly = true)]
            public partial record EndpointConfig
            {
                public string Url { get; init; } = default!;
            }
            """;

        var emptyConfig = new Dictionary<string, string?>();
        var (_, services) = GeneratorHarness.CompileAndAggregate(
            LibraryFixture,
            emptyConfig,
            validateOnStart: true);

        await using var sp = services.BuildServiceProvider();

        // Despite EndpointConfig being the only [ConfigSection] in the
        // compilation (so no in-compilation nested reference exists),
        // the IsNestedOnly=true flag classifies it as nested and the
        // aggregate wraps its registration in the .Exists() gate. An
        // absent __endpoint__ section is therefore silently skipped.
        await SimulateHostStartAsync(sp);
    }

    [Fact]
    public async Task With_flag_validation_fires_at_host_start()
    {
        // Arrange: same fixture, this time with the flag on. The generator's
        // emitted aggregate now chains AddOptions<DbConfig>().ValidateOnStart()
        // per registered section, which on .NET 10 registers an
        // IStartupValidator that the host invokes during StartAsync.
        var (_, services) = GeneratorHarness.CompileAndAggregate(
            FixtureSource,
            InvalidConfig(),
            validateOnStart: true);

        await using var sp = services.BuildServiceProvider();

        // Act + Assert: simulating host start must throw before any
        // IOptions<T>.Value read. This is the whole point of the flag —
        // bad config crashes the host during StartAsync, not on first
        // request. A plain Assert.ThrowsAsync<OptionsValidationException>
        // proves the exception type and the timing simultaneously.
        var ex = await Assert.ThrowsAsync<OptionsValidationException>(
            () => SimulateHostStartAsync(sp));

        Assert.Contains("[Db:Conn]", string.Join(" ", ex.Failures));
    }

    /// <summary>
    /// Mimics the observable parts of <see cref="IHost.StartAsync"/>:
    /// runs every registered <see cref="IHostedService"/> followed by
    /// every registered <see cref="IStartupValidator"/>. .NET 8 and 9 of
    /// <c>ValidateOnStart()</c> registered a <see cref="IHostedService"/>;
    /// .NET 10 switched to <see cref="IStartupValidator"/> and the real
    /// host invokes both. Iterating both here keeps the test portable
    /// across future framework changes without requiring a full
    /// <c>HostApplicationBuilder</c>.
    /// </summary>
    private static async Task SimulateHostStartAsync(IServiceProvider sp)
    {
        foreach (var hosted in sp.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }

        foreach (var validator in sp.GetServices<IStartupValidator>())
        {
            validator.Validate();
        }
    }
}
