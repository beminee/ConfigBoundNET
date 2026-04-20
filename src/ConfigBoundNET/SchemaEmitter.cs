// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace ConfigBoundNET;

/// <summary>
/// Renders a JSON Schema document (draft 2020-12) describing every
/// <c>[ConfigSection]</c>-annotated type in the current compilation, and
/// wraps the document inside a generated
/// <c>ConfigBoundNET.ConfigBoundJsonSchema</c> class as a <c>const string</c>.
/// </summary>
/// <remarks>
/// <para>
/// Roslyn source generators can only emit C# via <c>AddSource</c>; they
/// cannot drop an <c>appsettings.schema.json</c> file onto disk directly.
/// Exposing the schema as a <c>const</c> makes it trivially accessible at
/// runtime and also lets an optional downstream MSBuild task (Phase 2) read
/// the constant via <c>MetadataLoadContext</c> and write it to disk after
/// build.
/// </para>
/// <para>
/// The emitter is a pure function of its input
/// <see cref="ConfigSectionModel"/> array. Like <see cref="SourceEmitter"/>
/// it never touches <see cref="Microsoft.CodeAnalysis.Compilation"/> state
/// so the output is deterministic and value-equatable for the incremental
/// pipeline.
/// </para>
/// </remarks>
internal static class SchemaEmitter
{
    /// <summary>
    /// JSON Schema dialect emitted in <c>$schema</c>. Draft 2020-12 is
    /// recognised by VS Code, JetBrains Rider, Visual Studio 2022+, and
    /// <c>json-schema.org</c> itself.
    /// </summary>
    private const string Draft2020Url = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>The sentinel description written next to <c>writeOnly: true</c> for <c>[Sensitive]</c> properties.</summary>
    private const string SensitiveDescription = "Sensitive value — redacted in logs.";

    /// <summary>The permissive fallback emitted in place of a cross-assembly nested config.</summary>
    private const string ExternalNestedFallback = "{ \"type\": \"object\", \"additionalProperties\": true }";

    /// <summary>
    /// Produces the generated source file text: an <c>// &lt;auto-generated/&gt;</c>
    /// C# file declaring <c>ConfigBoundJsonSchema.Json</c> as a verbatim-string
    /// <c>const</c>. Diagnostics (currently only <c>CB0011</c>) are written
    /// into <paramref name="diagnostics"/> so the caller can report them
    /// from inside <c>RegisterSourceOutput</c>.
    /// </summary>
    /// <param name="models">
    /// Every successful <see cref="ConfigSectionModel"/> in the compilation,
    /// pre-sorted by the caller for deterministic output.
    /// </param>
    /// <param name="diagnostics">
    /// Output list the caller drains into <c>ReportDiagnostic</c>. May
    /// contain <see cref="DiagnosticInfo"/>s with <c>null</c> locations
    /// because the schema pipeline doesn't carry per-property syntax nodes.
    /// </param>
    public static string Emit(ImmutableArray<ConfigSectionModel> models, List<DiagnosticInfo> diagnostics)
    {
        var json = BuildSchemaJson(models, diagnostics);
        return WrapAsConstClass(json);
    }

    /// <summary>
    /// Produces just the schema document (no surrounding C# wrapper). Used
    /// by tests that want to parse and assert on the JSON directly.
    /// </summary>
    public static string EmitJsonOnly(ImmutableArray<ConfigSectionModel> models, List<DiagnosticInfo> diagnostics)
    {
        return BuildSchemaJson(models, diagnostics);
    }

    // ─────────────────────────────────────────────────────────────────────
    // JSON schema document construction
    // ─────────────────────────────────────────────────────────────────────

    private static string BuildSchemaJson(ImmutableArray<ConfigSectionModel> models, List<DiagnosticInfo> diagnostics)
    {
        // Index models by their fully-qualified type name so nested
        // [ConfigSection] references can resolve in O(1) during tree walk.
        // The key format (global::Namespace.TypeName) must match the FQN
        // that ModelBuilder.ClassifyBinding stores in
        // ConfigPropertyModel.NestedTypeFullyQualifiedName.
        var index = new Dictionary<string, ConfigSectionModel>(models.Length);
        foreach (var model in models)
        {
            index[BuildFullyQualifiedName(model)] = model;
        }

        var buffer = new StringBuilder();
        buffer.Append('{').AppendLine();

        // Preamble — "$schema", "title", "type", "additionalProperties".
        // additionalProperties: true at the root because appsettings.json
        // is shared with framework-reserved sections (Logging, AllowedHosts,
        // ConnectionStrings…) that ConfigBoundNET does not own.
        AppendIndent(buffer, 1);
        buffer.Append("\"$schema\": \"").Append(Draft2020Url).Append("\",").AppendLine();
        AppendIndent(buffer, 1);
        buffer.Append("\"title\": \"ConfigBoundNET generated schema\",").AppendLine();
        AppendIndent(buffer, 1);
        buffer.Append("\"type\": \"object\",").AppendLine();
        AppendIndent(buffer, 1);
        buffer.Append("\"additionalProperties\": true,").AppendLine();
        AppendIndent(buffer, 1);
        buffer.Append("\"properties\": {").AppendLine();

        // Emit one top-level property per [ConfigSection] model, keyed by
        // its SectionName. The runtime binder is happy with nested sections
        // (e.g. "Services:Payments") but for the JSON schema that reaches
        // editors, we only expose the first segment here — a colon-qualified
        // section like "A:B" gets emitted as "A:B" verbatim (editors tolerate
        // it, and the alternative — nesting — would over-constrain users who
        // already use nested objects in their JSON).
        for (int i = 0; i < models.Length; i++)
        {
            var model = models[i];
            AppendIndent(buffer, 2);
            buffer.Append('"').Append(JsonEscape(model.SectionName)).Append("\": ");
            WriteSectionSchema(buffer, 2, model, index, diagnostics);
            if (i < models.Length - 1)
            {
                buffer.Append(',');
            }

            buffer.AppendLine();
        }

        AppendIndent(buffer, 1);
        buffer.Append('}').AppendLine();
        buffer.Append('}');

        return buffer.ToString();
    }

    /// <summary>
    /// Writes the object-schema body for a single <see cref="ConfigSectionModel"/>.
    /// Emits <c>type: object</c>, the per-property schemas, and a
    /// <c>required</c> array built from <see cref="ConfigPropertyModel.IsRequired"/>.
    /// </summary>
    private static void WriteSectionSchema(
        StringBuilder buffer,
        int indent,
        ConfigSectionModel model,
        Dictionary<string, ConfigSectionModel> index,
        List<DiagnosticInfo> diagnostics)
    {
        buffer.Append('{').AppendLine();
        AppendIndent(buffer, indent + 1);
        buffer.Append("\"type\": \"object\",").AppendLine();

        // additionalProperties: false on each [ConfigSection] object catches
        // typos in config keys — the whole point of strongly-typed config
        // binding. We only relax this at the root (where appsettings.json
        // intermingles with framework sections).
        AppendIndent(buffer, indent + 1);
        buffer.Append("\"additionalProperties\": false");

        var properties = model.Properties;
        if (properties.Length > 0)
        {
            buffer.Append(',').AppendLine();
            AppendIndent(buffer, indent + 1);
            buffer.Append("\"properties\": {").AppendLine();

            for (int i = 0; i < properties.Length; i++)
            {
                var prop = properties[i];
                AppendIndent(buffer, indent + 2);
                buffer.Append('"').Append(JsonEscape(prop.Name)).Append("\": ");
                WritePropertySchema(buffer, indent + 2, model, prop, index, diagnostics);
                if (i < properties.Length - 1)
                {
                    buffer.Append(',');
                }

                buffer.AppendLine();
            }

            AppendIndent(buffer, indent + 1);
            buffer.Append('}');

            // required[] — names of every property whose IsRequired is true.
            // The schema uses first-class JSON Schema required semantics:
            // validators reject the document if a required key is missing.
            var requiredCount = CountRequired(properties);
            if (requiredCount > 0)
            {
                buffer.Append(',').AppendLine();
                AppendIndent(buffer, indent + 1);
                buffer.Append("\"required\": [");
                var first = true;
                for (int i = 0; i < properties.Length; i++)
                {
                    if (!properties[i].IsRequired)
                    {
                        continue;
                    }

                    if (!first)
                    {
                        buffer.Append(", ");
                    }

                    first = false;
                    buffer.Append('"').Append(JsonEscape(properties[i].Name)).Append('"');
                }

                buffer.Append(']');
            }
        }

        buffer.AppendLine();
        AppendIndent(buffer, indent);
        buffer.Append('}');
    }

    /// <summary>
    /// Writes the schema fragment for a single property. The fragment has
    /// the shape <c>{ "type": ..., optional attrs... }</c> and is always
    /// emitted on its own line sequence (caller handles trailing comma and
    /// newline).
    /// </summary>
    private static void WritePropertySchema(
        StringBuilder buffer,
        int indent,
        ConfigSectionModel owner,
        ConfigPropertyModel prop,
        Dictionary<string, ConfigSectionModel> index,
        List<DiagnosticInfo> diagnostics)
    {
        // Nested-config branches don't share the type/format treatment that
        // the scalar and collection branches do — they recurse straight into
        // another schema object (or fall back to a permissive {} if the
        // referenced type lives in another assembly).
        switch (prop.Binding)
        {
            case BindingStrategy.NestedConfig:
                WriteNestedObjectSchema(buffer, indent, owner, prop, prop.NestedTypeFullyQualifiedName, index, diagnostics);
                return;

            case BindingStrategy.NestedConfigCollection:
                buffer.Append('{').AppendLine();
                AppendIndent(buffer, indent + 1);
                buffer.Append("\"type\": \"array\",").AppendLine();
                AppendIndent(buffer, indent + 1);
                buffer.Append("\"items\": ");
                WriteNestedObjectSchema(buffer, indent + 1, owner, prop, prop.NestedTypeFullyQualifiedName, index, diagnostics);
                WriteCollectionLengthAnnotations(buffer, indent + 1, prop);
                WriteSensitiveAndDescription(buffer, indent + 1, prop);
                buffer.AppendLine();
                AppendIndent(buffer, indent);
                buffer.Append('}');
                return;

            case BindingStrategy.NestedConfigDictionary:
                buffer.Append('{').AppendLine();
                AppendIndent(buffer, indent + 1);
                buffer.Append("\"type\": \"object\",").AppendLine();
                AppendIndent(buffer, indent + 1);
                buffer.Append("\"additionalProperties\": ");
                WriteNestedObjectSchema(buffer, indent + 1, owner, prop, prop.NestedTypeFullyQualifiedName, index, diagnostics);
                WriteSensitiveAndDescription(buffer, indent + 1, prop);
                buffer.AppendLine();
                AppendIndent(buffer, indent);
                buffer.Append('}');
                return;

            case BindingStrategy.Array:
                buffer.Append('{').AppendLine();
                AppendIndent(buffer, indent + 1);
                buffer.Append("\"type\": \"array\",").AppendLine();
                AppendIndent(buffer, indent + 1);
                buffer.Append("\"items\": ");
                WriteScalarElementSchema(buffer, indent + 1, prop);
                WriteCollectionLengthAnnotations(buffer, indent + 1, prop);
                WriteSensitiveAndDescription(buffer, indent + 1, prop);
                buffer.AppendLine();
                AppendIndent(buffer, indent);
                buffer.Append('}');
                return;

            case BindingStrategy.Dictionary:
                buffer.Append('{').AppendLine();
                AppendIndent(buffer, indent + 1);
                buffer.Append("\"type\": \"object\",").AppendLine();
                AppendIndent(buffer, indent + 1);
                buffer.Append("\"additionalProperties\": ");
                WriteScalarElementSchema(buffer, indent + 1, prop);
                WriteSensitiveAndDescription(buffer, indent + 1, prop);
                buffer.AppendLine();
                AppendIndent(buffer, indent);
                buffer.Append('}');
                return;
        }

        // Scalar types: build an in-place object literal.
        buffer.Append('{').AppendLine();
        WriteScalarBody(buffer, indent + 1, prop);
        WriteSensitiveAndDescription(buffer, indent + 1, prop);
        buffer.AppendLine();
        AppendIndent(buffer, indent);
        buffer.Append('}');
    }

    /// <summary>
    /// Writes the type/format/enum/constraint body for a scalar property
    /// (single-line fragments separated by commas+newlines). Does <em>not</em>
    /// wrap in <c>{}</c>. Caller is responsible for the wrapping object.
    /// </summary>
    private static void WriteScalarBody(StringBuilder buffer, int indent, ConfigPropertyModel prop)
    {
        // Type + optional null for nullable value types.
        AppendIndent(buffer, indent);
        WriteTypeField(buffer, prop);

        // Format hints for well-known scalar types.
        var format = FormatFor(prop.Binding);
        if (format is not null)
        {
            buffer.Append(',').AppendLine();
            AppendIndent(buffer, indent);
            buffer.Append("\"format\": \"").Append(format).Append('"');
        }

        // Enum: fold member names into an "enum" array. When the user has
        // also provided [AllowedValues], that annotation takes over below.
        if (prop.Binding == BindingStrategy.Enum && prop.EnumMemberNames.Length > 0
            && !HasAllowedValuesAnnotation(prop))
        {
            buffer.Append(',').AppendLine();
            AppendIndent(buffer, indent);
            buffer.Append("\"enum\": [");
            var members = prop.EnumMemberNames;
            for (int i = 0; i < members.Length; i++)
            {
                if (i > 0)
                {
                    buffer.Append(", ");
                }

                buffer.Append('"').Append(JsonEscape(members[i])).Append('"');
            }

            buffer.Append(']');
        }

        WriteAnnotationConstraints(buffer, indent, prop);
    }

    /// <summary>
    /// Writes the object-shape schema for a single nested <c>[ConfigSection]</c>
    /// reference. When the referenced FQN cannot be resolved against the
    /// compilation-local index (typically because the target type lives in
    /// another assembly), emits a permissive <c>{}</c> fallback and reports
    /// <c>CB0011</c>.
    /// </summary>
    private static void WriteNestedObjectSchema(
        StringBuilder buffer,
        int indent,
        ConfigSectionModel owner,
        ConfigPropertyModel prop,
        string? nestedFqn,
        Dictionary<string, ConfigSectionModel> index,
        List<DiagnosticInfo> diagnostics)
    {
        // Nested FQNs preserve nullable-reference annotations (T?) for
        // collection element-type spelling consistency with the user's
        // declared property. Strip any trailing '?' before lookup so the
        // index key matches the un-annotated model FQN.
        var key = nestedFqn;
        if (key is not null && key.Length > 0 && key[key.Length - 1] == '?')
        {
            key = key.Substring(0, key.Length - 1);
        }

        if (key is not null && index.TryGetValue(key, out var nested))
        {
            WriteSectionSchema(buffer, indent, nested, index, diagnostics);
            return;
        }

        // External nested config — permissive fallback. Report CB0011 so
        // users are aware their schema is slightly degraded for this
        // property, without breaking builds.
        diagnostics.Add(new DiagnosticInfo(
            DiagnosticDescriptors.ExternalNestedConfigNotAnalyzed.Id,
            Location: null,
            MessageArgs: new EquatableArray<string>(new[]
            {
                nestedFqn ?? "<unknown>",
                owner.TypeName + "." + prop.Name,
            })));

        buffer.Append(ExternalNestedFallback);
    }

    /// <summary>
    /// Writes the schema for a single scalar element inside a collection or
    /// dictionary. Shares most of its logic with <see cref="WriteScalarBody"/>
    /// but consumes <see cref="ConfigPropertyModel.CollectionElementStrategy"/>
    /// and <see cref="ConfigPropertyModel.EnumMemberNames"/> (which belong
    /// to the element type for collection-of-enum).
    /// </summary>
    private static void WriteScalarElementSchema(StringBuilder buffer, int indent, ConfigPropertyModel prop)
    {
        var elemStrategy = prop.CollectionElementStrategy;
        buffer.Append('{').AppendLine();

        AppendIndent(buffer, indent + 1);
        buffer.Append("\"type\": \"").Append(JsonTypeFor(elemStrategy)).Append('"');

        var format = FormatFor(elemStrategy);
        if (format is not null)
        {
            buffer.Append(',').AppendLine();
            AppendIndent(buffer, indent + 1);
            buffer.Append("\"format\": \"").Append(format).Append('"');
        }

        if (elemStrategy == BindingStrategy.Enum && prop.EnumMemberNames.Length > 0)
        {
            buffer.Append(',').AppendLine();
            AppendIndent(buffer, indent + 1);
            buffer.Append("\"enum\": [");
            var members = prop.EnumMemberNames;
            for (int i = 0; i < members.Length; i++)
            {
                if (i > 0)
                {
                    buffer.Append(", ");
                }

                buffer.Append('"').Append(JsonEscape(members[i])).Append('"');
            }

            buffer.Append(']');
        }

        buffer.AppendLine();
        AppendIndent(buffer, indent);
        buffer.Append('}');
    }

    /// <summary>
    /// Emits the DataAnnotations-driven constraint fragments:
    /// <c>minimum</c>/<c>maximum</c> from <c>[Range]</c>, <c>minLength</c>/<c>maxLength</c>
    /// from <c>[StringLength]</c> etc., <c>pattern</c> from <c>[RegularExpression]</c>,
    /// <c>format</c> from <c>[Url]</c>/<c>[EmailAddress]</c>, <c>enum</c>/<c>not.enum</c>
    /// from <c>[AllowedValues]</c>/<c>[DeniedValues]</c>.
    /// </summary>
    /// <remarks>
    /// A custom <see cref="DataAnnotationModel.ErrorMessage"/> becomes the
    /// <c>description</c> field when no description is set yet. The
    /// <c>[Sensitive]</c> description (handled separately in
    /// <see cref="WriteSensitiveAndDescription"/>) takes precedence.
    /// </remarks>
    private static void WriteAnnotationConstraints(StringBuilder buffer, int indent, ConfigPropertyModel prop)
    {
        var annotations = prop.DataAnnotations;
        if (annotations.Length == 0)
        {
            return;
        }

        string? descriptionFromErrorMessage = null;

        foreach (var ann in annotations)
        {
            switch (ann.Kind)
            {
                case DataAnnotationKind.Range:
                    if (ann.NumericArg1.HasValue)
                    {
                        buffer.Append(',').AppendLine();
                        AppendIndent(buffer, indent);
                        buffer.Append("\"minimum\": ").Append(FormatJsonNumber(ann.NumericArg1.Value, prop.ParseTypeKeyword));
                    }

                    if (ann.NumericArg2.HasValue)
                    {
                        buffer.Append(',').AppendLine();
                        AppendIndent(buffer, indent);
                        buffer.Append("\"maximum\": ").Append(FormatJsonNumber(ann.NumericArg2.Value, prop.ParseTypeKeyword));
                    }

                    break;

                case DataAnnotationKind.StringLength:
                    if (ann.NumericArg1.HasValue)
                    {
                        buffer.Append(',').AppendLine();
                        AppendIndent(buffer, indent);
                        buffer.Append(prop.IsString ? "\"maxLength\": " : "\"maxItems\": ");
                        buffer.Append(FormatJsonInteger(ann.NumericArg1.Value));
                    }

                    if (ann.NumericArg2.HasValue)
                    {
                        buffer.Append(',').AppendLine();
                        AppendIndent(buffer, indent);
                        buffer.Append(prop.IsString ? "\"minLength\": " : "\"minItems\": ");
                        buffer.Append(FormatJsonInteger(ann.NumericArg2.Value));
                    }

                    break;

                case DataAnnotationKind.MinLength:
                    if (ann.NumericArg1.HasValue)
                    {
                        buffer.Append(',').AppendLine();
                        AppendIndent(buffer, indent);
                        buffer.Append(prop.IsString ? "\"minLength\": " : "\"minItems\": ");
                        buffer.Append(FormatJsonInteger(ann.NumericArg1.Value));
                    }

                    break;

                case DataAnnotationKind.MaxLength:
                    if (ann.NumericArg1.HasValue)
                    {
                        buffer.Append(',').AppendLine();
                        AppendIndent(buffer, indent);
                        buffer.Append(prop.IsString ? "\"maxLength\": " : "\"maxItems\": ");
                        buffer.Append(FormatJsonInteger(ann.NumericArg1.Value));
                    }

                    break;

                case DataAnnotationKind.RegularExpression:
                    if (ann.StringArg1 is { Length: > 0 } pattern)
                    {
                        buffer.Append(',').AppendLine();
                        AppendIndent(buffer, indent);
                        buffer.Append("\"pattern\": \"").Append(JsonEscape(pattern)).Append('"');
                    }

                    break;

                case DataAnnotationKind.Url:
                    buffer.Append(',').AppendLine();
                    AppendIndent(buffer, indent);
                    buffer.Append("\"format\": \"uri\"");
                    break;

                case DataAnnotationKind.EmailAddress:
                    buffer.Append(',').AppendLine();
                    AppendIndent(buffer, indent);
                    buffer.Append("\"format\": \"email\"");
                    break;

                case DataAnnotationKind.AllowedValues:
                    buffer.Append(',').AppendLine();
                    AppendIndent(buffer, indent);
                    buffer.Append("\"enum\": [");
                    WriteValuesArray(buffer, ann.ValuesArg, prop);
                    buffer.Append(']');
                    break;

                case DataAnnotationKind.DeniedValues:
                    buffer.Append(',').AppendLine();
                    AppendIndent(buffer, indent);
                    buffer.Append("\"not\": { \"enum\": [");
                    WriteValuesArray(buffer, ann.ValuesArg, prop);
                    buffer.Append("] }");
                    break;

                case DataAnnotationKind.Required:
                    // Redundant with IsRequired — already folded into the
                    // owning type's required[] array in WriteSectionSchema.
                    break;
            }

            if (ann.ErrorMessage is { Length: > 0 } && descriptionFromErrorMessage is null)
            {
                descriptionFromErrorMessage = ann.ErrorMessage;
            }
        }

        if (descriptionFromErrorMessage is not null && !prop.IsSensitive)
        {
            buffer.Append(',').AppendLine();
            AppendIndent(buffer, indent);
            buffer.Append("\"description\": \"").Append(JsonEscape(descriptionFromErrorMessage)).Append('"');
        }
    }

    /// <summary>
    /// Writes <c>writeOnly: true</c> and the redaction description for
    /// <c>[Sensitive]</c> properties. Override any description previously
    /// written from an <c>ErrorMessage</c>: sensitivity is more important
    /// for the user to know than a bounds-violation message.
    /// </summary>
    private static void WriteSensitiveAndDescription(StringBuilder buffer, int indent, ConfigPropertyModel prop)
    {
        if (!prop.IsSensitive)
        {
            return;
        }

        buffer.Append(',').AppendLine();
        AppendIndent(buffer, indent);
        buffer.Append("\"writeOnly\": true,").AppendLine();
        AppendIndent(buffer, indent);
        buffer.Append("\"description\": \"").Append(JsonEscape(SensitiveDescription)).Append('"');
    }

    /// <summary>
    /// Emits <c>minItems</c> / <c>maxItems</c> for collections annotated
    /// with <c>[MinLength]</c> / <c>[MaxLength]</c>. Shares the annotation
    /// array with <see cref="WriteAnnotationConstraints"/> but lives on the
    /// outer array object (not on the <c>items</c> schema).
    /// </summary>
    private static void WriteCollectionLengthAnnotations(StringBuilder buffer, int indent, ConfigPropertyModel prop)
    {
        foreach (var ann in prop.DataAnnotations)
        {
            switch (ann.Kind)
            {
                case DataAnnotationKind.MinLength:
                    if (ann.NumericArg1.HasValue)
                    {
                        buffer.Append(',').AppendLine();
                        AppendIndent(buffer, indent);
                        buffer.Append("\"minItems\": ").Append(FormatJsonInteger(ann.NumericArg1.Value));
                    }

                    break;

                case DataAnnotationKind.MaxLength:
                    if (ann.NumericArg1.HasValue)
                    {
                        buffer.Append(',').AppendLine();
                        AppendIndent(buffer, indent);
                        buffer.Append("\"maxItems\": ").Append(FormatJsonInteger(ann.NumericArg1.Value));
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Writes the <c>type</c> field for a scalar property. For nullable
    /// value types (<c>int?</c>, <c>MyEnum?</c>, …) emits the two-element
    /// form <c>["integer", "null"]</c> so validators accept explicit JSON
    /// <c>null</c>.
    /// </summary>
    private static void WriteTypeField(StringBuilder buffer, ConfigPropertyModel prop)
    {
        var baseType = JsonTypeFor(prop.Binding);
        if (prop.IsNullableValueType)
        {
            buffer.Append("\"type\": [\"").Append(baseType).Append("\", \"null\"]");
        }
        else
        {
            buffer.Append("\"type\": \"").Append(baseType).Append('"');
        }
    }

    /// <summary>
    /// Maps every <see cref="BindingStrategy"/> onto the base JSON Schema
    /// <c>type</c> string. Nested/collection/dictionary strategies produce
    /// <c>"object"</c> / <c>"array"</c> wrappers handled directly by the
    /// switch in <see cref="WritePropertySchema"/>; the cases here exist so
    /// the function is total and remains compile-safe when a new
    /// <see cref="BindingStrategy"/> is added.
    /// </summary>
    private static string JsonTypeFor(BindingStrategy strategy) => strategy switch
    {
        BindingStrategy.String => "string",
        BindingStrategy.Boolean => "boolean",
        BindingStrategy.Integer => "integer",
        BindingStrategy.FloatingPoint => "number",
        BindingStrategy.Guid => "string",
        BindingStrategy.TimeSpan => "string",
        BindingStrategy.DateTime => "string",
        BindingStrategy.DateTimeOffset => "string",
        BindingStrategy.Uri => "string",
        BindingStrategy.Enum => "string",
        BindingStrategy.NestedConfig => "object",
        BindingStrategy.Array => "array",
        BindingStrategy.Dictionary => "object",
        BindingStrategy.NestedConfigCollection => "array",
        BindingStrategy.NestedConfigDictionary => "object",
        _ => "string",
    };

    /// <summary>Maps well-known scalar types to JSON Schema <c>format</c> hints.</summary>
    private static string? FormatFor(BindingStrategy strategy) => strategy switch
    {
        BindingStrategy.Guid => "uuid",
        BindingStrategy.DateTime => "date-time",
        BindingStrategy.DateTimeOffset => "date-time",
        BindingStrategy.Uri => "uri",
        _ => null,
    };

    /// <summary>
    /// Renders a <c>double</c> as a JSON numeric literal. Integer-typed
    /// properties (see <see cref="ConfigPropertyModel.ParseTypeKeyword"/>)
    /// always emit without a decimal point.
    /// </summary>
    private static string FormatJsonNumber(double value, string? parseTypeKeyword)
    {
        if (parseTypeKeyword is "byte" or "sbyte" or "short" or "ushort"
            or "int" or "uint" or "long" or "ulong")
        {
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        }

        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Renders a <c>double</c> as a JSON integer literal. Used for
    /// length/item-count annotations which are conceptually integers even
    /// though they flow through the shared <c>NumericArg1</c>/<c>NumericArg2</c>
    /// fields as <c>double</c>s.
    /// </summary>
    private static string FormatJsonInteger(double value) =>
        ((long)value).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes the comma-separated contents of a <c>[AllowedValues]</c> /
    /// <c>[DeniedValues]</c> list as JSON literals. Strings and enums are
    /// quoted; numerics and booleans are emitted raw.
    /// </summary>
    private static void WriteValuesArray(StringBuilder buffer, EquatableArray<string> values, ConfigPropertyModel prop)
    {
        // Strings and enums both render as JSON strings. For numerics/booleans
        // the TypedConstant.ToString() produces a correctly-formatted literal
        // already (e.g. "42", "true") which is valid JSON as-is.
        var quoteAsString = prop.IsString
            || prop.Binding == BindingStrategy.Enum
            || prop.Binding == BindingStrategy.Guid
            || prop.Binding == BindingStrategy.Uri
            || prop.Binding == BindingStrategy.TimeSpan
            || prop.Binding == BindingStrategy.DateTime
            || prop.Binding == BindingStrategy.DateTimeOffset;

        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                buffer.Append(", ");
            }

            if (quoteAsString)
            {
                buffer.Append('"').Append(JsonEscape(values[i])).Append('"');
            }
            else
            {
                buffer.Append(values[i]);
            }
        }
    }

    /// <summary>Counts required properties without allocating a list.</summary>
    private static int CountRequired(EquatableArray<ConfigPropertyModel> properties)
    {
        var count = 0;
        for (int i = 0; i < properties.Length; i++)
        {
            if (properties[i].IsRequired)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the property carries an
    /// <c>[AllowedValues]</c> annotation — in which case the
    /// <see cref="BindingStrategy.Enum"/> branch should not redundantly
    /// emit its own <c>enum</c> fragment (the annotation's values take
    /// precedence).
    /// </summary>
    private static bool HasAllowedValuesAnnotation(ConfigPropertyModel prop)
    {
        foreach (var ann in prop.DataAnnotations)
        {
            if (ann.Kind == DataAnnotationKind.AllowedValues)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Computes the fully-qualified type name used as the nested-type
    /// index key. Must match the spelling that
    /// <see cref="ModelBuilder.ClassifyBinding"/> stores in
    /// <see cref="ConfigPropertyModel.NestedTypeFullyQualifiedName"/>.
    /// </summary>
    private static string BuildFullyQualifiedName(ConfigSectionModel model) =>
        model.Namespace is null
            ? "global::" + model.TypeName
            : "global::" + model.Namespace + "." + model.TypeName;

    // ─────────────────────────────────────────────────────────────────────
    // C# wrapper + JSON escaping + indentation
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Wraps the schema document inside a C# file that declares
    /// <c>ConfigBoundNET.ConfigBoundJsonSchema.Json</c> as a verbatim
    /// <c>const string</c>. The verbatim form lets us embed the JSON
    /// verbatim (including whitespace) without backslash-escaping the whole
    /// document; only double-quotes need doubling.
    /// </summary>
    public static string WrapAsConstClass(string json)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("//     This file was generated by ConfigBoundNET.");
        sb.AppendLine("//     Changes to this file will be lost when the generator re-runs.");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace ConfigBoundNET;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Exposes the JSON Schema (draft 2020-12) describing every");
        sb.AppendLine("/// <c>[ConfigSection]</c>-annotated type in this assembly. Reference it");
        sb.AppendLine("/// from your <c>appsettings.json</c> via <c>\"$schema\":</c> to get");
        sb.AppendLine("/// IntelliSense and validation red-squiggles in VS Code, Visual Studio,");
        sb.AppendLine("/// and Rider.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class ConfigBoundJsonSchema");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>The JSON Schema document, emitted at build time.</summary>");
        sb.Append("    public const string Json = @\"");
        // Verbatim string: the only escape needed is "" for embedded ".
        sb.Append(json.Replace("\"", "\"\""));
        sb.AppendLine("\";");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Escapes a string for inclusion inside a JSON double-quoted literal
    /// per RFC 8259 §7. Handles the control characters (<c>\b</c>, <c>\f</c>,
    /// <c>\n</c>, <c>\r</c>, <c>\t</c>) explicitly and emits <c>\uXXXX</c>
    /// for the remaining U+0000–U+001F range.
    /// </summary>
    private static string JsonEscape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Appends <paramref name="level"/> × 2 spaces to <paramref name="sb"/>.</summary>
    private static void AppendIndent(StringBuilder sb, int level)
    {
        for (int i = 0; i < level; i++)
        {
            sb.Append("  ");
        }
    }
}
