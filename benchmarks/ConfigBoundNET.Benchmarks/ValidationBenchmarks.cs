// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using BenchmarkDotNet.Attributes;
using ConfigBoundNET.Benchmarks.Configs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ConfigBoundNET.Benchmarks;

/// <summary>
/// Measures the per-call cost of running the generated validator against a
/// pre-bound options instance — the cost every app pays at startup when
/// <c>ValidateOnStart()</c> is chained on the options builder.
/// </summary>
/// <remarks>
/// Setup binds each type once in <c>GlobalSetup</c> so the benchmark
/// methods isolate validator-call cost only. Two source-generated paths
/// compared:
/// <list type="bullet">
///   <item><description>
///     <c>ConfigBoundNET_Validate</c> — ConfigBoundNET's generated
///     <c>Validator</c> nested class. Explicit <c>if</c>-chain over every
///     DataAnnotation. No reflection, no <c>Validator.TryValidateObject</c>.
///   </description></item>
///   <item><description>
///     <c>MicrosoftSourceGen_Validate</c> —
///     <see cref="MicrosoftAppConfigValidator"/> populated by Microsoft's
///     <c>[OptionsValidator]</c> generator. Also explicit and reflection-free.
///   </description></item>
/// </list>
/// <para>
/// Both run against valid instances, so both return
/// <see cref="ValidateOptionsResult.Success"/>; measurement focuses on the
/// check-every-property cost, not failure-message construction.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class ValidationBenchmarks
{
    private ConfigBoundAppConfig _configBoundOptions = default!;
    private ConfigBoundAppConfig.Validator _configBoundValidator = default!;
    private MicrosoftAppConfig _microsoftOptions = default!;
    private MicrosoftAppConfigValidator _microsoftValidator = default!;

    [GlobalSetup]
    public void Setup()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();

        // Bind each options instance once, then freeze it for the hot loop.
        _configBoundOptions = new ConfigBoundAppConfig(configuration.GetSection("App"));
        _microsoftOptions = configuration.GetSection("AppMs").Get<MicrosoftAppConfig>()!;

        // Validator instances are also frozen — matches real DI where the
        // validator is registered as a singleton.
        _configBoundValidator = new ConfigBoundAppConfig.Validator();
        _microsoftValidator = new MicrosoftAppConfigValidator();
    }

    [Benchmark(Baseline = true, Description = "ConfigBoundNET: generated Validator.Validate")]
    public ValidateOptionsResult ConfigBoundNET_Validate()
    {
        return _configBoundValidator.Validate(name: null, _configBoundOptions);
    }

    [Benchmark(Description = "Microsoft: [OptionsValidator] generated Validate")]
    public ValidateOptionsResult MicrosoftSourceGen_Validate()
    {
        return _microsoftValidator.Validate(name: null, _microsoftOptions);
    }
}
