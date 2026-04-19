// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

namespace ConfigBoundNET;

/// <summary>
/// Stable names for the generator's incremental pipeline steps, used by
/// <c>IncrementalValueProvider.WithTrackingName</c> in
/// <see cref="ConfigBoundGenerator"/> and by the cache/perf tests that
/// assert on <c>GeneratorDriverRunResult.TrackedSteps</c>.
/// </summary>
/// <remarks>
/// Keeping the names in one place means the test project (which has
/// <c>InternalsVisibleTo</c> access) can reference the same constants as the
/// generator. Changing a name is safe because the constant is internal and
/// only used by us — but keep the set small: every tracking name allocates
/// a small amount of per-run bookkeeping inside Roslyn.
/// </remarks>
internal static class TrackingNames
{
    /// <summary>
    /// Tracking name for the transform step that turns a
    /// <c>GeneratorAttributeSyntaxContext</c> (one per <c>[ConfigSection]</c>
    /// type) into a flat, equatable <see cref="BuildResult"/>.
    /// </summary>
    internal const string BuildResults = nameof(BuildResults);

    /// <summary>
    /// Tracking name for the transform step that projects each successful
    /// <see cref="BuildResult"/> into a minimal <see cref="AggregateEntry"/>
    /// (just namespace + type name) for the assembly-wide
    /// <c>AddConfigBoundSections</c> emitter. Carrying only two strings
    /// keeps the aggregate pipeline cache-stable across unrelated per-type
    /// edits (e.g. adding a <c>[Range]</c> annotation).
    /// </summary>
    internal const string AggregateEntries = nameof(AggregateEntries);
}
