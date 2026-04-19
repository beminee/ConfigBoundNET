// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using BenchmarkDotNet.Attributes;
using ConfigBoundNET.Benchmarks.Configs;
using Microsoft.Extensions.Configuration;

namespace ConfigBoundNET.Benchmarks;

/// <summary>
/// Measures the per-call cost of materialising a strongly-typed options
/// instance from a live <see cref="IConfigurationSection"/> — the hot path
/// every config-bound .NET app hits at startup.
/// </summary>
/// <remarks>
/// Two source-generated paths compared head-to-head:
/// <list type="bullet">
///   <item><description>
///     <c>ConfigBoundNET_Bind</c> — ConfigBoundNET's generated
///     <c>(IConfigurationSection)</c> constructor. Explicit per-property
///     <c>section["Key"]</c> reads + <c>TryParse</c> calls, no reflection.
///   </description></item>
///   <item><description>
///     <c>MicrosoftSourceGen_Bind</c> — Microsoft's
///     <c>EnableConfigurationBindingGenerator</c> path. The
///     <c>section.Get&lt;T&gt;()</c> call is intercepted at compile time and
///     rewired to generated AOT-friendly binding code (also no reflection).
///   </description></item>
/// </list>
/// <para>
/// Both are AOT-safe and generated; measurement isolates the difference in
/// machinery (explicit constructor call vs generated binder extension
/// method) rather than generator-vs-reflection, which would be an unfair
/// comparison.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class BindingBenchmarks
{
    private IConfigurationSection _configBoundSection = default!;
    private IConfigurationSection _microsoftSection = default!;

    [GlobalSetup]
    public void Setup()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();

        // Two parallel sections ("App" for ConfigBoundNET, "AppMs" for
        // Microsoft) so neither benchmark's binding influences the other's
        // internal caches. Both sections carry identical content.
        _configBoundSection = configuration.GetSection("App");
        _microsoftSection = configuration.GetSection("AppMs");
    }

    [Benchmark(Baseline = true, Description = "ConfigBoundNET: generated (IConfigurationSection) ctor")]
    public ConfigBoundAppConfig ConfigBoundNET_Bind()
    {
        return new ConfigBoundAppConfig(_configBoundSection);
    }

    [Benchmark(Description = "Microsoft: EnableConfigurationBindingGenerator (section.Get<T>)")]
    public MicrosoftAppConfig? MicrosoftSourceGen_Bind()
    {
        return _microsoftSection.Get<MicrosoftAppConfig>();
    }
}
