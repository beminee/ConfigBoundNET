// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System;

namespace ConfigBoundNET;

/// <summary>
/// Shared helper for deriving a configuration section name from a type name.
/// Used by both the generator (<see cref="ModelBuilder"/>) and the
/// <c>EmptySectionNameCodeFix</c> in the CodeFixes project.
/// </summary>
internal static class SectionNameHelper
{
    /// <summary>
    /// Suffixes stripped from the type name, tried longest-first so
    /// <c>Configuration</c> is attempted before <c>Config</c>.
    /// </summary>
    private static readonly string[] Suffixes =
        { "Configuration", "Settings", "Options", "Config" };

    /// <summary>
    /// Derives a configuration section name from a type name by stripping
    /// the first matching common suffix. Returns the full type name if no
    /// suffix matches or if stripping would leave an empty string.
    /// </summary>
    /// <example>
    /// <c>"DbConfig"</c> returns <c>"Db"</c>;
    /// <c>"PaymentsOptions"</c> returns <c>"Payments"</c>;
    /// <c>"Config"</c> returns <c>"Config"</c> (stripping would leave empty).
    /// </example>
    internal static string InferSectionName(string typeName)
    {
        foreach (var suffix in Suffixes)
        {
            if (typeName.Length > suffix.Length &&
                typeName.EndsWith(suffix, StringComparison.Ordinal))
            {
                return typeName.Substring(0, typeName.Length - suffix.Length);
            }
        }

        return typeName;
    }
}
