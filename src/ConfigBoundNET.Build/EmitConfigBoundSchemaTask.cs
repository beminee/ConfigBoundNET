// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace ConfigBoundNET.Build;

/// <summary>
/// MSBuild task that extracts the JSON schema produced by the ConfigBoundNET
/// source generator (embedded as a <c>const string</c> on
/// <c>ConfigBoundNET.ConfigBoundJsonSchema.Json</c>) and writes it to a
/// file on disk.
/// </summary>
/// <remarks>
/// <para>
/// The task uses <see cref="MetadataLoadContext"/> rather than
/// <see cref="Assembly.LoadFrom(string)"/>: the target assembly is loaded
/// reflection-only, so no user code runs, no module initializer fires, and
/// MSBuild's own AppDomain stays uncontaminated. The only thing the task
/// needs from the assembly is the raw value of a compile-time constant,
/// which <see cref="FieldInfo.GetRawConstantValue"/> exposes without
/// executing any IL.
/// </para>
/// <para>
/// The task is wired into the consumer's build via
/// <c>build/ConfigBoundNET.targets</c> (shipped in the generator's NuGet).
/// That target applies the <c>AfterTargets="Build"</c> hook and sets
/// <c>Inputs</c>/<c>Outputs</c> so an unchanged assembly skips the task
/// entirely.
/// </para>
/// </remarks>
public sealed class EmitConfigBoundSchemaTask : Microsoft.Build.Utilities.Task
{
    /// <summary>
    /// The full path to the just-built assembly. Usually
    /// <c>$(TargetPath)</c> from MSBuild.
    /// </summary>
    [Required]
    public string AssemblyPath { get; set; } = string.Empty;

    /// <summary>
    /// The full path where the schema file should be written. Usually
    /// <c>$(ConfigBoundSchemaOutputPath)</c>, which the target defaults to
    /// <c>$(MSBuildProjectDirectory)/appsettings.schema.json</c>.
    /// </summary>
    [Required]
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>
    /// Additional assembly search directories. The task seeds the
    /// <see cref="PathAssemblyResolver"/> with these plus the target
    /// assembly's directory and the currently-executing runtime directory,
    /// which is enough to load the closed-world reference graph under
    /// MetadataLoadContext.
    /// </summary>
    /// <remarks>
    /// MSBuild populates this from <c>@(ReferencePath)</c> in the targets
    /// file. Paths that do not exist are silently skipped; MetadataLoadContext
    /// tolerates missing entries in the resolver search set.
    /// </remarks>
    public ITaskItem[] ReferencePaths { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The fully qualified metadata name of the generated schema holder type.</summary>
    private const string SchemaTypeName = "ConfigBoundNET.ConfigBoundJsonSchema";

    /// <summary>The name of the public <c>const string</c> field that holds the schema JSON.</summary>
    private const string SchemaFieldName = "Json";

    /// <inheritdoc />
    public override bool Execute()
    {
        if (string.IsNullOrWhiteSpace(AssemblyPath) || !File.Exists(AssemblyPath))
        {
            Log.LogError(
                "ConfigBoundNET schema emission: AssemblyPath '{0}' does not exist.",
                AssemblyPath);
            return false;
        }

        string? schemaJson;
        try
        {
            schemaJson = ReadSchemaFromAssembly(AssemblyPath, ReferencePaths);
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            // Wrap everything short of fatal CLR errors into a single build
            // error so failures surface cleanly in MSBuild's output panel
            // instead of as an unhandled exception stack dump.
            Log.LogError(
                "ConfigBoundNET schema emission: failed to read schema constant from '{0}': {1}",
                AssemblyPath,
                ex.Message);
            return false;
        }

        if (schemaJson is null)
        {
            // No [ConfigSection] types → generator skipped the schema output
            // → no ConfigBoundJsonSchema class in the assembly. Not an error:
            // the target's whole purpose is to sync the schema when one
            // exists. Surface an info message so the user can correlate
            // "schema file missing" with "nothing to emit".
            Log.LogMessage(
                MessageImportance.Low,
                "ConfigBoundNET schema emission: assembly '{0}' contains no ConfigBoundJsonSchema type; skipping.",
                AssemblyPath);
            return true;
        }

        try
        {
            WriteIfChanged(OutputPath, schemaJson);
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            Log.LogError(
                "ConfigBoundNET schema emission: failed to write '{0}': {1}",
                OutputPath,
                ex.Message);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Loads <paramref name="assemblyPath"/> under a
    /// <see cref="MetadataLoadContext"/>, reads the
    /// <c>ConfigBoundJsonSchema.Json</c> const value, and returns it.
    /// Returns <see langword="null"/> when the type or field is absent
    /// (i.e. the consumer's assembly has no <c>[ConfigSection]</c> types).
    /// </summary>
    private static string? ReadSchemaFromAssembly(string assemblyPath, ITaskItem[] referencePaths)
    {
        var searchPaths = BuildSearchPaths(assemblyPath, referencePaths);
        var resolver = new PathAssemblyResolver(searchPaths);

        using var context = new MetadataLoadContext(resolver);
        var assembly = context.LoadFromAssemblyPath(assemblyPath);

        // GetType accepts throwOnError=false; when absent, the consumer
        // simply had nothing to describe. Either the generator wasn't
        // wired in (probable misconfiguration) or the compilation had no
        // [ConfigSection] types (legitimate early-adoption case).
        var schemaType = assembly.GetType(SchemaTypeName, throwOnError: false, ignoreCase: false);
        if (schemaType is null)
        {
            return null;
        }

        // BindingFlags.DeclaredOnly guards against a shadowed field on a
        // hypothetical derived type. Public | Static matches the generator's
        // output exactly.
        var field = schemaType.GetField(
            SchemaFieldName,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        if (field is null)
        {
            return null;
        }

        // GetRawConstantValue returns the metadata-embedded literal without
        // executing any code. For a string const this is a plain string.
        return field.GetRawConstantValue() as string;
    }

    /// <summary>
    /// Builds the set of full assembly paths that the metadata loader uses
    /// to resolve assembly references it encounters while inspecting
    /// <paramref name="assemblyPath"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="PathAssemblyResolver"/> requires that each assembly
    /// <em>identity</em> appears only once in the search set. That rules
    /// out bulk directory scans: a typical <c>bin/</c> contains several
    /// DLLs that claim the same identity (e.g. <c>mscorlib.dll</c>
    /// shim + <c>System.Private.CoreLib.dll</c>, or BCL facades that
    /// forward to the same implementation). Instead we trust
    /// <c>@(ReferencePath)</c> — the exact, de-duplicated set the
    /// compiler resolved the consumer's project against — and add only
    /// the target assembly on top.
    /// </para>
    /// <para>
    /// One more entry is always added: the runtime's <c>mscorlib.dll</c>
    /// if present and not already in the reference set. On .NET Core /
    /// .NET 5+ hosts this is actually <c>System.Private.CoreLib.dll</c>,
    /// which is fine: <see cref="PathAssemblyResolver"/> matches
    /// whichever assembly in the set claims the <c>mscorlib</c> identity
    /// at resolve time.
    /// </para>
    /// </remarks>
    private static List<string> BuildSearchPaths(string assemblyPath, ITaskItem[] referencePaths)
    {
        var result = new List<string>();

        // HashSet to de-dup at path level (not identity level — that's the
        // resolver's job, but redundant path entries confuse it). Ordinal
        // ignore-case matches Windows filesystem semantics.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddFile(assemblyPath, seen, result);

        foreach (var item in referencePaths)
        {
            var itemSpec = item.ItemSpec;
            if (!string.IsNullOrWhiteSpace(itemSpec))
            {
                AddFile(itemSpec, seen, result);
            }
        }

        // Safety net for hosts that don't flow @(ReferencePath) through:
        // grab the currently-executing runtime's core assembly by file
        // name only. If ReferencePaths already contains it, the seen-set
        // filter drops the duplicate.
        var coreAssembly = typeof(object).Assembly.Location;
        if (!string.IsNullOrEmpty(coreAssembly))
        {
            AddFile(coreAssembly, seen, result);
        }

        return result;
    }

    /// <summary>Adds a single file to <paramref name="accumulator"/> if it exists and hasn't been seen.</summary>
    private static void AddFile(string candidate, HashSet<string> seen, List<string> accumulator)
    {
        if (!string.IsNullOrWhiteSpace(candidate)
            && File.Exists(candidate)
            && seen.Add(candidate))
        {
            accumulator.Add(candidate);
        }
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> only
    /// when the file does not already exist with identical content. This
    /// keeps the file's last-write timestamp stable across no-op rebuilds,
    /// which is exactly what MSBuild's <c>Inputs</c>/<c>Outputs</c>
    /// incremental-build contract expects (a rebuild-as-noop should NOT
    /// bump mtime, or every downstream target retriggers).
    /// </summary>
    private void WriteIfChanged(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path, Encoding.UTF8);
            if (string.Equals(existing, content, StringComparison.Ordinal))
            {
                Log.LogMessage(
                    MessageImportance.Low,
                    "ConfigBoundNET schema emission: '{0}' already up to date.",
                    path);
                return;
            }
        }

        // UTF-8 without BOM: matches what VS Code and Visual Studio emit by
        // default and avoids confusing diff tools that render "FEFF" as a
        // leading glyph in the file preview. JSON is UTF-8 by spec (RFC 8259).
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Log.LogMessage(
            MessageImportance.Normal,
            "ConfigBoundNET schema emission: wrote '{0}'.",
            path);
    }

    /// <summary>
    /// Decides whether an exception is one we want to let propagate rather
    /// than wrap in an <c>LogError</c> call. Matches the conservative set
    /// that the BCL itself treats as "unrecoverable".
    /// </summary>
    private static bool IsFatal(Exception ex) =>
        ex is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException;
}
