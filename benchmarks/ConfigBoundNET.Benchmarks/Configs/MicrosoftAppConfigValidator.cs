// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using Microsoft.Extensions.Options;

namespace ConfigBoundNET.Benchmarks.Configs;

/// <summary>
/// Microsoft's <c>[OptionsValidator]</c> source generator fills the
/// <see cref="IValidateOptions{TOptions}.Validate"/> body from the
/// DataAnnotations on <see cref="MicrosoftAppConfig"/>. This is the
/// competitive target for <c>ConfigBoundAppConfig.Validator</c>.
/// </summary>
[OptionsValidator]
public sealed partial class MicrosoftAppConfigValidator : IValidateOptions<MicrosoftAppConfig>
{
}
