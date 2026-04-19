// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

// BenchmarkSwitcher gives CLI-driven selection out of the box:
//   dotnet run -c Release                       (run every benchmark class)
//   dotnet run -c Release -- --filter '*Bind*'  (only binding benchmarks)
//   dotnet run -c Release -- --list tree        (list available benchmarks)
//
// BDN's default artifacts path is relative to the current working directory,
// which lands them at the repo root when invoked via `dotnet run --project ...`.
// We pin the artifacts path to a folder adjacent to the csproj itself (up
// three levels from bin/<Config>/<TFM>/) so results always land beside the
// benchmark project regardless of where the user is when they run it.
var artifactsPath = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "BenchmarkDotNet.Artifacts"));

var config = DefaultConfig.Instance.WithArtifactsPath(artifactsPath);

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);

// Marker type so FromAssembly has something to anchor on under top-level
// statements. Partial because the compiler synthesises its own Program type
// for the top-level statements above; our partial declaration merges with it.
internal sealed partial class Program;
