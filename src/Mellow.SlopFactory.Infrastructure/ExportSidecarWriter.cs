using System.Text;
using System.Text.Json;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Infrastructure;

/// <summary>
/// Builds a `.slopfactory.json` sidecar document (plan.md:663-761) for one exported
/// <see cref="FileRecord"/>. Privacy-minimal by default: only <see cref="ExportSidecarOptions"/>'s
/// explicit opt-ins add anything beyond identity/size/hash/timestamps and (if the file has a known
/// originating generation) a label-only provider/model snapshot and provenance state. Deterministic:
/// the same inputs always produce byte-identical output — properties are always written in the same
/// fixed order (never reflection/dictionary-driven), UTF-8 without a BOM, LF line endings, two-space
/// indentation.
/// </summary>
internal static class ExportSidecarWriter
{
    /// <summary>Matches the `$id` in the published schema doc (docs/developer/slopfactory-sidecar.schema.json).
    /// A future breaking change to the emitted shape must bump both this and
    /// <see cref="SidecarSchemaVersion"/> together (plan.md:670).</summary>
    public const string SchemaId = "https://slopfactory.app/schema/sidecar/v1.json";
    public const int SidecarSchemaVersion = 1;

    public static string BuildJson(FileRecord file, GenerationRecord? generation, ExportSidecarOptions options)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("$schema", SchemaId);
            writer.WriteNumber("sidecarSchemaVersion", SidecarSchemaVersion);
            writer.WriteString("mediaType", file.MediaType);
            writer.WriteNumber("byteSize", file.ByteSize);
            writer.WriteString("contentHash", file.ContentHash);
            writer.WriteString("importedAt", file.ImportedAt);
            writer.WriteString("modifiedAt", file.ModifiedAt);
            writer.WriteString("origin", file.Origin.ToString());

            if (options.IncludeFilenames)
            {
                writer.WriteString("displayName", file.DisplayName);
                writer.WriteString("originalFileName", file.OriginalFileName);
            }

            if (options.IncludeInternalIdentifiers)
            {
                // Never provider request IDs — this app never persists one anywhere to begin with,
                // so that exclusion (plan.md:705) is automatically satisfied, not a filtered field.
                writer.WriteString("fileId", file.Id);
                if (generation is not null) writer.WriteString("generationRecordId", generation.Id);
                if (generation?.ModelId is { } modelId) writer.WriteString("modelId", modelId);
            }

            if (generation is not null)
            {
                writer.WriteString("providerType", generation.ProviderType.ToString());
                writer.WriteString("modelLabel", generation.ModelLabel);
                writer.WriteString("generationProvenanceState", ProvenanceState(generation));
                if (generation.CompletedAt is { } completedAt) writer.WriteString("statusObservedAt", completedAt);

                if (options.IncludePrompt)
                {
                    writer.WriteString("prompt", generation.Prompt);
                    if (generation.SystemInstructions is { } systemInstructions) writer.WriteString("systemInstructions", systemInstructions);
                }

                if (options.IncludeUsageAndCost)
                {
                    if (generation.ActualCost is { } cost) writer.WriteNumber("actualCost", cost);
                    if (generation.ActualCostCurrency is { } currency) writer.WriteString("actualCostCurrency", currency);
                    if (generation.PromptTokens is { } promptTokens) writer.WriteNumber("promptTokens", promptTokens);
                    if (generation.CompletionTokens is { } completionTokens) writer.WriteNumber("completionTokens", completionTokens);
                }

                if (options.IncludeAdvancedSettings)
                {
                    WriteAdvancedSettings(writer, generation.Settings);
                }
            }

            if (options.IncludeSensitiveMetadata)
            {
                // Reserved: this app has no metadata-entries feature yet for a sidecar to draw on.
                // Deliberately a documented no-op rather than guessing a shape for a feature that
                // doesn't exist, exactly like IncludeSafetyMetadata below.
                writer.WriteBoolean("sensitiveMetadataUnavailable", true);
            }

            if (options.IncludeSafetyMetadata)
            {
                // Blocked on IMPLEMENTATION_COMPLETION_CHECKLIST.md Section 10 ("Complete provider
                // safety behavior") — no persisted, content-hash-bound safety classification exists
                // anywhere in this app yet for a sidecar to read. Documented no-op, not a silent
                // omission: the toggle is honored as "nothing available," never silently ignored.
                writer.WriteBoolean("safetyMetadataUnavailable", true);
            }

            writer.WriteEndObject();
        }

        var utf8 = stream.ToArray();
        var text = Encoding.UTF8.GetString(utf8);
        // Utf8JsonWriter emits '\n' between properties already on every platform (it does not use
        // Environment.NewLine), but normalize explicitly since plan.md:665 requires LF
        // unconditionally and this is the one detail worth not simply trusting.
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return text;
    }

    private static void WriteAdvancedSettings(Utf8JsonWriter writer, GenerationSettings settings)
    {
        writer.WriteStartObject("generationSettings");
        if (settings.Temperature is { } temperature) writer.WriteNumber("temperature", temperature);
        if (settings.TopP is { } topP) writer.WriteNumber("topP", topP);
        if (settings.MaxTokens is { } maxTokens) writer.WriteNumber("maxTokens", maxTokens);
        if (settings.FrequencyPenalty is { } frequencyPenalty) writer.WriteNumber("frequencyPenalty", frequencyPenalty);
        if (settings.PresencePenalty is { } presencePenalty) writer.WriteNumber("presencePenalty", presencePenalty);
        var advancedPreview = LibraryRules.BuildAdvancedGenerationSettingsPreview(settings);
        if (advancedPreview is not null)
        {
            writer.WritePropertyName("advancedJson");
            using var advancedDocument = JsonDocument.Parse(advancedPreview);
            advancedDocument.RootElement.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    /// <summary>plan.md:698-700's four provenance states. <c>current</c> means the generation's
    /// terminal outcome is exactly what's reflected here; the others flag that the sidecar is
    /// describing something less certain than "this is the live, current record."</summary>
    private static string ProvenanceState(GenerationRecord generation)
    {
        if (generation.State == LibraryRecordState.Recycled) return "history-recycled";
        if (!LibraryRules.IsTerminalGenerationStatus(generation.Status)) return "nonterminal-snapshot";
        return "current";
    }
}
