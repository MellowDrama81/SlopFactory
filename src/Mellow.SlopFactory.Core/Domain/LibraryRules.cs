using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Mellow.SlopFactory.Domain;

public static class LibraryRules
{
    public const string FormatIdentity = "mellow.slopfactory.library";
    public const int ManifestVersion = 1;
    public const int SchemaVersion = 41;
    public const int MaximumDisplayNameScalars = 255;
    public const int MaximumMetadataKeyScalars = 100;
    public const int MaximumLinkLabelScalars = 200;
    public const int MaximumLabelScalars = 100;
    public const int MaximumTagNameScalars = 60;
    public const int MaximumMetadataEntriesPerFile = 1_000;
    public const int MaximumMetadataValueUtf8Bytes = 1_048_576;
    public const int MaximumEditableTextUtf8Bytes = 4_194_304;
    public const int MaximumGenerationTextUtf8Bytes = 1_048_576;
    /// <summary>Largest individual provider result the current in-memory transfer pipeline accepts.
    /// Downloads are rejected while reading, even when the server omits or lies about Content-Length.</summary>
    public const long MaximumProviderResultBytes = 536_870_912;

    /// <summary>
    /// The current version of the normalized settings snapshot shape (<see
    /// cref="GenerationSettings"/> plus how a provider adapter interprets it) written into new
    /// <see cref="GenerationRecord"/> and <see cref="SavedGenerationSetting"/> rows — signed adapter
    /// versioning for normalized snapshot formats, so a record
    /// stays tagged with the format it was actually written under even after a later signed
    /// application update changes how that format is produced or interpreted.
    /// A historical record's own <c>SettingsFormatVersion</c> is never rewritten in place — only a
    /// freshly created or updated record (including one built from **Use Again**, since that reads
    /// old values but writes a brand-new request interpreted by whichever adapter version is active
    /// now) gets the current value. Bump this only when a future adapter change alters how
    /// <see cref="GenerationSettings"/>/advanced JSON must be interpreted in a way that would make an
    /// older record misread under the new adapter; add the matching interpretation/migration logic
    /// at the same time so an older-versioned record remains correctly readable rather than merely
    /// distinguishable.
    /// </summary>
    public const int CurrentGenerationSettingsFormatVersion = 1;

    public static string ValidateGenerationTextLength(string value, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Encoding.UTF8.GetByteCount(value) > MaximumGenerationTextUtf8Bytes)
        {
            throw new LibraryValidationException($"{fieldName} cannot exceed {MaximumGenerationTextUtf8Bytes} UTF-8 bytes.");
        }

        return value;
    }
    public const int MaximumRenderedMarkdownCharacters = 262_144;
    public const int MaximumTextSearchScalars = 256;
    public static readonly TimeSpan ModelCatalogueStalenessPeriod = TimeSpan.FromDays(7);
    public const int MinimumConnectionTimeoutSeconds = 5;
    public const int MaximumConnectionTimeoutSeconds = 600;
    public const int DefaultConnectionTimeoutSeconds = 100;

    public static int? NormalizeConnectionTimeoutSeconds(int? value)
    {
        if (value is null) return null;
        if (value < MinimumConnectionTimeoutSeconds || value > MaximumConnectionTimeoutSeconds)
        {
            throw new LibraryValidationException($"Connection timeout must be between {MinimumConnectionTimeoutSeconds} and {MaximumConnectionTimeoutSeconds} seconds, or left blank to use the default of {DefaultConnectionTimeoutSeconds} seconds.");
        }
        return value;
    }

    public const double MinTemperature = 0.0;
    public const double MaxTemperature = 2.0;
    public const double MinTopP = 0.0;
    public const double MaxTopP = 1.0;
    public const int MinMaxTokens = 1;
    public const double MinFrequencyPenalty = -2.0;
    public const double MaxFrequencyPenalty = 2.0;
    public const double MinPresencePenalty = -2.0;
    public const double MaxPresencePenalty = 2.0;
    public const int MaximumAdvancedGenerationSettingsJsonBytes = 65_536;

    private static readonly HashSet<string> ReservedAdvancedGenerationSettingKeys = new(StringComparer.Ordinal)
    {
        "model", "messages", "n", "stream", "temperature", "top_p", "max_tokens", "frequency_penalty", "presence_penalty"
    };

    private static readonly string[] SensitiveAdvancedGenerationSettingKeyFragments = ["api_key", "apikey", "authorization", "token", "secret", "password"];

    public static GenerationSettings ValidateGenerationSettings(GenerationSettings settings)
    {
        if (settings.Temperature is { } temperature && (temperature < MinTemperature || temperature > MaxTemperature))
        {
            throw new LibraryValidationException($"Temperature must be between {MinTemperature} and {MaxTemperature}, or left blank to use the provider default.");
        }
        if (settings.TopP is { } topP && (topP < MinTopP || topP > MaxTopP))
        {
            throw new LibraryValidationException($"Top P must be between {MinTopP} and {MaxTopP}, or left blank to use the provider default.");
        }
        if (settings.MaxTokens is { } maxTokens && maxTokens < MinMaxTokens)
        {
            throw new LibraryValidationException($"Max tokens must be at least {MinMaxTokens}, or left blank to use the provider default.");
        }
        if (settings.FrequencyPenalty is { } frequencyPenalty && (frequencyPenalty < MinFrequencyPenalty || frequencyPenalty > MaxFrequencyPenalty))
        {
            throw new LibraryValidationException($"Frequency penalty must be between {MinFrequencyPenalty} and {MaxFrequencyPenalty}, or left blank to use the provider default.");
        }
        if (settings.PresencePenalty is { } presencePenalty && (presencePenalty < MinPresencePenalty || presencePenalty > MaxPresencePenalty))
        {
            throw new LibraryValidationException($"Presence penalty must be between {MinPresencePenalty} and {MaxPresencePenalty}, or left blank to use the provider default.");
        }
        if (string.IsNullOrWhiteSpace(settings.AdvancedJson)) return settings with { AdvancedJson = null };
        if (Encoding.UTF8.GetByteCount(settings.AdvancedJson) > MaximumAdvancedGenerationSettingsJsonBytes)
        {
            throw new LibraryValidationException($"Advanced generation settings must not exceed {MaximumAdvancedGenerationSettingsJsonBytes:N0} UTF-8 bytes.");
        }

        try
        {
            using var document = JsonDocument.Parse(settings.AdvancedJson, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new LibraryValidationException("Advanced generation settings must be a JSON object.");
            }
            ValidateJsonElement(document.RootElement, 0, new JsonNodeCounter());
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (ReservedAdvancedGenerationSettingKeys.Contains(property.Name))
                {
                    throw new LibraryValidationException($"Advanced generation settings cannot override the managed '{property.Name}' field.");
                }
                ValidateKnownAdvancedGenerationSettingType(property);
            }
            return settings with { AdvancedJson = document.RootElement.GetRawText() };
        }
        catch (JsonException exception)
        {
            throw new LibraryValidationException($"Advanced generation settings must be valid JSON: {exception.Message}");
        }
    }

    private static void ValidateKnownAdvancedGenerationSettingType(JsonProperty property)
    {
        var valid = property.Name switch
        {
            "response_format" => property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.String,
            "seed" or "top_logprobs" => property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out _),
            "logprobs" or "parallel_tool_calls" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "user" => property.Value.ValueKind == JsonValueKind.String,
            "stop" => property.Value.ValueKind == JsonValueKind.String ||
                      property.Value.ValueKind == JsonValueKind.Array && property.Value.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String),
            _ => true
        };
        if (!valid)
        {
            throw new LibraryValidationException($"Advanced generation setting '{property.Name}' has an invalid JSON type.");
        }
    }

    /// <summary>Returns a normalized local preview of advanced request settings, redacting values
    /// whose key names conventionally contain credentials. Validation intentionally runs first so a
    /// malformed object never gets presented as a sendable request.</summary>
    public static string? BuildAdvancedGenerationSettingsPreview(GenerationSettings settings)
    {
        var normalized = ValidateGenerationSettings(settings);
        if (normalized.AdvancedJson is null) return null;
        using var document = JsonDocument.Parse(normalized.AdvancedJson);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            WriteSanitizedJsonElement(writer, document.RootElement, redactValue: false);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteSanitizedJsonElement(Utf8JsonWriter writer, JsonElement element, bool redactValue)
    {
        if (redactValue)
        {
            writer.WriteStringValue("[redacted]");
            return;
        }
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    var sensitive = SensitiveAdvancedGenerationSettingKeyFragments.Any(fragment => property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
                    WriteSanitizedJsonElement(writer, property.Value, sensitive);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var child in element.EnumerateArray()) WriteSanitizedJsonElement(writer, child, redactValue: false);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>
    /// Which named source-input slot roles a model accepts, and how many. Deliberately a small
    /// switch, not a stored/persisted schema: only the capabilities actually confirmed against a
    /// provider's real API are represented here (text generation's up-to-3 reference images, any
    /// provider; image-to-image editing for OpenAI's, DeepInfra's, and OpenRouter's confirmed
    /// contracts — OpenAI and DeepInfra share the identical <c>images/edits</c> multipart shape,
    /// OpenRouter uses its own <c>input_references</c> field; DeepInfra video's optional first-frame
    /// image). 1min.ai and the generic OpenAI-compatible adapter have no confirmed image-editing
    /// contract, so Image mode declares nothing for them — their <c>GenerateImageAsync</c> ignores any
    /// source images that reach it regardless (see
    /// <see cref="Application.IProviderAdapter.GenerateImageAsync"/>). Adding a further confirmed
    /// provider capability later is one more arm here, not a data migration — see
    /// <see cref="GenerationInputSlotRole"/>'s own remarks.
    /// <para>
    /// DeepInfra's image-edit <c>MaxCount</c> is a flat 3, same as OpenAI/OpenRouter, deliberately not
    /// keyed per model: live testing (2026-08-19, see <c>DeepInfraProviderAdapter.GenerateImageAsync</c>'s
    /// remarks) found actual multi-image behavior varies a lot by model and even by run —
    /// <c>black-forest-labs/FLUX.1-Kontext-dev</c> reliably keeps only the last image, while
    /// <c>black-forest-labs/FLUX-2-klein-9b</c> genuinely uses more than one but with real run-to-run
    /// quality variance — and a per-model allowlist here proved to be exactly the kind of unverifiable,
    /// high-maintenance special-casing this schema is meant to avoid (any new or unlisted DeepInfra
    /// image-edit model would silently get the conservative default rather than what its own API
    /// actually supports). Offering the slots and letting a per-model quirk surface as an ordinary
    /// generation-quality or provider-error outcome is preferred over trying to hard-code every model's
    /// behavior in this switch.
    /// </para>
    /// </summary>
    public static IReadOnlyList<GenerationInputSlotCapability> GetInputSlotCapabilities(ProviderType providerType, GenerationMode mode) => mode switch
    {
        // Anthropic, Gemini and Cohere all genuinely support image input in chat (vision), but none of
        // their bespoke adapters translate TextGenerationSourceImage into that provider's own
        // content-block shape in this pass — offering the slot here without that translation would let
        // a user attach an image that silently never reaches the provider, which is worse than not
        // offering the slot at all. Revisit once translation is implemented for one of them.
        GenerationMode.Text when providerType is not (ProviderType.Anthropic or ProviderType.Gemini or ProviderType.Cohere) =>
            [new GenerationInputSlotCapability(GenerationInputSlotRole.ReferenceImage, 0, 3, Required: false)],
        GenerationMode.Image when providerType is ProviderType.OpenAi or ProviderType.OpenRouter or ProviderType.DeepInfra => [new GenerationInputSlotCapability(GenerationInputSlotRole.ReferenceImage, 0, 3, Required: false)],
        // ComfyUi's real per-slot capability lives in the model's own workflow JSON (whether it wires a
        // LoadImage node to {{UPLOADED_IMAGE_FILENAME}}/{{UPLOADED_IMAGE_FILENAME_2}} or not), which this
        // flat (provider, mode) switch cannot see. This declares a fixed upper bound instead (Comfy.md
        // section 3.3, option (a)) — a pure text-to-image workflow with no such node simply ignores an
        // offered slot. 2 is the highest reference-image count any built-in workflow template actually
        // uses today (see ComfyBuiltInWorkflows) — a dual-image-edit graph with two LoadImage nodes.
        GenerationMode.Image when providerType == ProviderType.ComfyUi => [new GenerationInputSlotCapability(GenerationInputSlotRole.ReferenceImage, 0, 2, Required: false)],
        GenerationMode.Video when providerType == ProviderType.DeepInfra => [new GenerationInputSlotCapability(GenerationInputSlotRole.FirstFrame, 0, 1, Required: false)],
        _ => []
    };

    /// <summary>The same file selected in more than one source slot is always rejected, independent
    /// of any model's capabilities — this still applies even when no model is selected yet (e.g. a
    /// draft with no model chosen), unlike the role/count checks in <see cref="ValidateSourceSlots"/>
    /// which need a model's capabilities to mean anything.</summary>
    public static void ValidateNoDuplicateSourceSlotFiles(IReadOnlyList<GenerationSourceSlot> slots)
    {
        var seenFileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            if (!seenFileIds.Add(slot.FileId))
            {
                throw new LibraryValidationException("The same source file cannot be selected in more than one source slot.");
            }
        }
    }

    /// <summary>Validates a proposed source-slot assignment against a model's capabilities: rejects
    /// a role the model doesn't declare, a role assigned more files than its
    /// <see cref="GenerationInputSlotCapability.MaxCount"/>, a <see cref="GenerationInputSlotCapability.Required"/>
    /// role with no assignment, and (via <see cref="ValidateNoDuplicateSourceSlotFiles"/>) the same
    /// file selected in more than one slot.</summary>
    public static void ValidateSourceSlots(IReadOnlyList<GenerationSourceSlot> slots, IReadOnlyList<GenerationInputSlotCapability> capabilities)
    {
        ValidateNoDuplicateSourceSlotFiles(slots);

        foreach (var group in slots.GroupBy(slot => slot.Role))
        {
            var capability = capabilities.FirstOrDefault(candidate => candidate.Role == group.Key);
            if (capability is null)
            {
                throw new LibraryValidationException($"The selected model does not accept a '{group.Key}' source input.");
            }

            var count = group.Count();
            if (count > capability.MaxCount)
            {
                throw new LibraryValidationException($"The selected model accepts at most {capability.MaxCount} '{group.Key}' source file(s).");
            }
        }

        foreach (var capability in capabilities.Where(candidate => candidate.Required))
        {
            if (!slots.Any(slot => slot.Role == capability.Role))
            {
                throw new LibraryValidationException($"The selected model requires a '{capability.Role}' source input.");
            }
        }
    }

    /// <summary>
    /// Which <see cref="GenerationSettings"/> fields a provider+mode combination actually transmits.
    /// Deliberately a small switch, not a stored/persisted schema, mirroring
    /// <see cref="GetInputSlotCapabilities"/>: every OpenAI-compatible-shaped adapter (OpenAI, the
    /// generic OpenAI-compatible adapter, OpenRouter, DeepInfra, and the Mistral/Groq/Together
    /// AI/Fireworks AI/DeepSeek/Perplexity/xAI/AI21 batch) reuses
    /// <c>OpenAiCompatibleProtocol.BuildChatCompletionRequestBody</c>, which sends every one of these
    /// fields when present. 1min.AI's native chat endpoint accepts a <see cref="GenerationSettings"/>
    /// parameter but never reads it. Anthropic and Gemini's bespoke request shapes have no
    /// frequency/presence-penalty equivalent, so those two flags are withheld for them; Cohere's v1
    /// chat request does support both (<c>frequency_penalty</c>/<c>presence_penalty</c>), so it gets
    /// the full set like the OpenAI-compatible group. No adapter's Image/Audio/Video request builder
    /// accepts <see cref="GenerationSettings"/> at all, so every non-Text mode has no capabilities
    /// regardless of provider.
    /// </summary>
    public static GenerationSettingsCapability GetGenerationSettingsCapabilities(ProviderType providerType, GenerationMode mode)
    {
        if (mode != GenerationMode.Text) return GenerationSettingsCapability.None;
        if (providerType == ProviderType.OneMinAi) return GenerationSettingsCapability.None;
        if (providerType is ProviderType.Anthropic or ProviderType.Gemini)
        {
            return GenerationSettingsCapability.Temperature | GenerationSettingsCapability.TopP | GenerationSettingsCapability.MaxTokens
                | GenerationSettingsCapability.AdvancedJson;
        }
        return GenerationSettingsCapability.Temperature | GenerationSettingsCapability.TopP | GenerationSettingsCapability.MaxTokens
            | GenerationSettingsCapability.FrequencyPenalty | GenerationSettingsCapability.PresencePenalty | GenerationSettingsCapability.AdvancedJson;
    }

    /// <summary>
    /// Whether a provider's Audio-mode adapter accepts a caller-chosen preset voice identifier.
    /// Deliberately a small switch, not a stored/persisted schema, mirroring
    /// <see cref="GetInputSlotCapabilities"/> and <see cref="GetGenerationSettingsCapabilities"/>:
    /// DeepInfra's, OpenAI's, and Groq's confirmed <c>POST .../audio/speech</c> contracts (the OpenAI
    /// Audio API shape, reused verbatim by <see cref="Infrastructure.Providers.OpenAiProviderAdapter"/>/
    /// <see cref="Infrastructure.Providers.OpenRouterProviderAdapter"/>/
    /// <see cref="Infrastructure.Providers.DeepInfraProviderAdapter"/>/
    /// <see cref="Infrastructure.Providers.GroqProviderAdapter"/>) all document an optional/required
    /// <c>voice</c> field; Gemini's TTS accepts a <c>speechConfig.voiceConfig.prebuiltVoiceConfig.voiceName</c>
    /// (see <see cref="Infrastructure.Providers.GoogleGeminiProviderAdapter.GenerateAudioAsync"/>) —
    /// conceptually the same "pick a preset voice" capability, just a different field path. No other
    /// adapter's audio request documents one.
    /// </summary>
    public static bool SupportsAudioVoiceSelection(ProviderType providerType) =>
        providerType is ProviderType.DeepInfra or ProviderType.OpenAi or ProviderType.OpenRouter or ProviderType.Groq or ProviderType.Gemini;

    /// <summary>
    /// Whether a provider has any model-listing endpoint at all. Deliberately a small switch, not a
    /// stored/persisted schema, mirroring <see cref="GetInputSlotCapabilities"/>/
    /// <see cref="SupportsAudioVoiceSelection"/>: only 1min.ai has no documented model-listing
    /// endpoint anywhere in its API reference (see
    /// <c>OneMinAiProviderAdapter.ListModelsAsync</c>, which always throws) — every other adapter
    /// implements a real <c>GET</c> equivalent. Lets <c>ModelEdit.razor</c> disable the discovery
    /// button and dropdown up front, with an explanation, instead of letting the user trigger a call
    /// that is guaranteed to fail.
    /// </summary>
    public static bool SupportsModelDiscovery(ProviderType providerType) => providerType is not (ProviderType.OneMinAi or ProviderType.ComfyUi);

    /// <summary>
    /// Validates a <see cref="Model.ComfyWorkflowTemplate"/> value: must be present (non-null/non-blank)
    /// whenever <paramref name="providerType"/> is <see cref="ProviderType.ComfyUi"/> (every ComfyUi
    /// model needs a workflow to submit), must parse as a JSON object keyed by numeric-string ComfyUI
    /// node IDs each holding <c>class_type</c>/<c>inputs</c> (the documented API-format shape a user
    /// exports via ComfyUI's "Save (API format)" button — see Comfy.md section 3.2), must contain the
    /// required <c>{{PROMPT}}</c> placeholder token at least once, and is bounded by the same
    /// <see cref="MaximumGenerationTextUtf8Bytes"/> size used for prompts/candidates elsewhere in this
    /// file — a workflow JSON can legitimately be large (many nodes), but an unbounded one is still a
    /// mistake to accept silently. For every non-ComfyUi provider, the value must be null (this field
    /// has no meaning outside ComfyUi and is never persisted for another provider's models).
    /// <para>
    /// The structural JSON check parses a <b>probe</b> string with every placeholder token replaced by
    /// a harmless dummy literal, not <paramref name="value"/> itself — <c>{{SEED}}</c> is meant to sit
    /// in an unquoted numeric position (e.g. <c>"seed": {{SEED}}</c>), which is not valid JSON on its
    /// own before substitution. The stored/returned value is always the original, unsubstituted text.
    /// </para>
    /// </summary>
    public static string? ValidateComfyWorkflowTemplate(string? value, GenerationMode mode)
    {
        _ = mode;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new LibraryValidationException("A ComfyUI model requires a workflow template.");
        }

        if (Encoding.UTF8.GetByteCount(value) > MaximumGenerationTextUtf8Bytes)
        {
            throw new LibraryValidationException($"Workflow template cannot exceed {MaximumGenerationTextUtf8Bytes} UTF-8 bytes.");
        }

        var probe = value
            .Replace("{{PROMPT}}", "probe-prompt", StringComparison.Ordinal)
            .Replace("{{NEGATIVE_PROMPT}}", "probe-negative-prompt", StringComparison.Ordinal)
            .Replace("{{UPLOADED_IMAGE_FILENAME}}", "probe-image.png", StringComparison.Ordinal)
            .Replace("{{SEED}}", "0", StringComparison.Ordinal);

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(probe).RootElement;
        }
        catch (JsonException)
        {
            throw new LibraryValidationException("Workflow template must be valid JSON.");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new LibraryValidationException("Workflow template must be a JSON object keyed by ComfyUI node IDs.");
        }

        foreach (var property in root.EnumerateObject())
        {
            if (!IsValidComfyNodeKey(property.Name))
            {
                throw new LibraryValidationException($"Workflow template node key '{property.Name}' is not a numeric ComfyUI node ID.");
            }

            if (property.Value.ValueKind != JsonValueKind.Object || !property.Value.TryGetProperty("class_type", out _) || !property.Value.TryGetProperty("inputs", out _))
            {
                throw new LibraryValidationException($"Workflow template node '{property.Name}' must be an object with 'class_type' and 'inputs'.");
            }
        }

        if (!value.Contains("{{PROMPT}}", StringComparison.Ordinal))
        {
            throw new LibraryValidationException("Workflow template must contain the {{PROMPT}} placeholder at least once.");
        }

        return value;
    }

    /// <summary>A real ComfyUI API-format export doesn't always use a bare integer as a node ID — a
    /// node inside a subgraph gets a colon-separated compound key instead (e.g. <c>"57:8"</c> for a
    /// node nested one subgraph deep, <c>"48:35:43"</c> for two deep — confirmed against real Comfy
    /// Cloud template exports). Accepts any key that is one or more colon-separated non-negative
    /// integer segments, rejecting only genuinely non-numeric keys.</summary>
    private static bool IsValidComfyNodeKey(string key)
    {
        foreach (var segment in key.Split(':'))
        {
            if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out _)) return false;
        }

        return true;
    }

    /// <summary>
    /// Computes a pre-generation cost estimate from real, just-fetched provider pricing — never
    /// bundled/guessed data (see <see cref="ProviderModelPricing"/>'s own remarks). The lower bound is
    /// deterministic (<paramref name="promptTokens"/> is itself only a rough local estimate — see
    /// <see cref="EstimateTokenCount"/> — but the multiplication itself is exact). The upper bound is
    /// only "reliable" (using the reliable upper bound of a range) when
    /// <paramref name="maxCompletionTokens"/> reflects a real configured cap; with no cap configured,
    /// there is no honest upper bound to show, so <see cref="GenerationCostEstimate.UpperBound"/>
    /// equals the lower bound and <see cref="GenerationCostEstimate.HasReliableUpperBound"/> is
    /// <see langword="false"/> — callers must show that distinction rather than presenting the equal
    /// bounds as a confirmed total.
    /// </summary>
    public static GenerationCostEstimate? EstimateGenerationCost(ProviderModelPricing? pricing, int promptTokens, int? maxCompletionTokens, string source, DateTimeOffset effectiveAt)
    {
        if (pricing is null || promptTokens < 0) return null;
        var lower = pricing.PromptCostPerToken * promptTokens;
        var hasReliableUpperBound = maxCompletionTokens is > 0;
        var upper = hasReliableUpperBound ? lower + pricing.CompletionCostPerToken * maxCompletionTokens!.Value : lower;
        return new GenerationCostEstimate(lower, upper, hasReliableUpperBound, pricing.Currency, source, effectiveAt);
    }

    public static int EstimateTokenCount(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));

    /// <summary>Whether a <see cref="GenerationStatus"/> never returns to an active state — a
    /// terminal generation cannot be advanced further, no matter what.</summary>
    public static bool IsTerminalGenerationStatus(GenerationStatus status) => status switch
    {
        GenerationStatus.Completed => true,
        GenerationStatus.Failed => true,
        GenerationStatus.PartiallyCompleted => true,
        GenerationStatus.Cancelled => true,
        GenerationStatus.CancelledWithResults => true,
        GenerationStatus.CompletedBeforeCancellation => true,
        GenerationStatus.CancelledBeforeSubmission => true,
        _ => false
    };

    public const int MaximumAdditionalConnectionHeaders = 10;
    public const int MaximumConnectionHeaderValueScalars = 500;

    private static readonly string[] ReservedConnectionHeaderNames =
    [
        "host", "content-length", "content-type", "transfer-encoding", "connection", "upgrade", "te", "trailer", "expect",
        "authorization", "proxy-authorization", "cookie", "set-cookie"
    ];

    public static IReadOnlyList<ConnectionHeader> NormalizeConnectionHeaders(IReadOnlyList<ConnectionHeader>? headers, string credentialHeaderName)
    {
        if (headers is null || headers.Count == 0) return [];
        if (headers.Count > MaximumAdditionalConnectionHeaders)
        {
            throw new LibraryValidationException($"No more than {MaximumAdditionalConnectionHeaders} additional headers are permitted.");
        }

        var normalized = new List<ConnectionHeader>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            var name = header.Name.Trim();
            if (name.Length == 0) throw new LibraryValidationException("Additional header names cannot be blank.");
            if (ReservedConnectionHeaderNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new LibraryValidationException($"'{name}' is a reserved or credential-only header and cannot be configured as an additional header.");
            }
            if (string.Equals(name, credentialHeaderName, StringComparison.OrdinalIgnoreCase))
            {
                throw new LibraryValidationException($"'{name}' is already used as the credential header and cannot also be configured as an additional header.");
            }
            if (!seenNames.Add(name))
            {
                throw new LibraryValidationException($"Additional header '{name}' is configured more than once.");
            }

            var value = header.Value ?? string.Empty;
            if (value.EnumerateRunes().Count() > MaximumConnectionHeaderValueScalars)
            {
                throw new LibraryValidationException($"Additional header values cannot exceed {MaximumConnectionHeaderValueScalars} characters.");
            }

            normalized.Add(new ConnectionHeader(name, value));
        }

        return normalized;
    }

    public static string? NormalizeRelativePathOverride(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim().TrimStart('/');
        if (trimmed.Length == 0) return null;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out _))
        {
            throw new LibraryValidationException($"{fieldName} must be a relative path, not an absolute URL.");
        }
        if (trimmed.Contains("..", StringComparison.Ordinal))
        {
            throw new LibraryValidationException($"{fieldName} cannot contain '..' segments.");
        }
        return trimmed;
    }

    public static GenericConnectionModalitySettings NormalizeGenericModalitySettings(GenericConnectionModalitySettings? settings)
    {
        if (settings is null) return GenericConnectionModalitySettings.Default;
        return settings with
        {
            ModelsPathOverride = NormalizeRelativePathOverride(settings.ModelsPathOverride, "Model-listing path"),
            TextGenerationPathOverride = NormalizeRelativePathOverride(settings.TextGenerationPathOverride, "Text-generation path"),
            ImageGenerationPathOverride = NormalizeRelativePathOverride(settings.ImageGenerationPathOverride, "Image-generation path")
        };
    }

    public const int MaximumInlineImageBytes = 33_554_432;

    public static string NewId() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    public static string NormalizeDisplayName(string value, string fieldName = "Name")
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = value.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Length == 0)
        {
            throw new LibraryValidationException($"{fieldName} is required.");
        }

        if (normalized.EnumerateRunes().Count() > MaximumDisplayNameScalars)
        {
            throw new LibraryValidationException($"{fieldName} exceeds {MaximumDisplayNameScalars} Unicode characters.");
        }

        if (normalized.IndexOfAny(['/', '\\']) >= 0 || normalized.Any(char.IsControl))
        {
            throw new LibraryValidationException($"{fieldName} contains unsupported characters.");
        }

        return normalized;
    }

    public static string NormalizeMetadataKey(string value)
    {
        var normalized = NormalizeLabel(value, MaximumMetadataKeyScalars, "Metadata key", allowLineBreaks: false);
        if (normalized.StartsWith("slopfactory.", StringComparison.OrdinalIgnoreCase))
        {
            throw new LibraryValidationException("The slopfactory. metadata prefix is reserved.");
        }

        return normalized;
    }

    public static string NormalizeLinkLabel(string value) =>
        NormalizeLabel(value, MaximumLinkLabelScalars, "Link label", allowLineBreaks: false);

    public static string NormalizeShortLabel(string value, string fieldName) =>
        NormalizeLabel(value, MaximumLabelScalars, fieldName, allowLineBreaks: false);

    public static string NormalizeTagName(string value) =>
        NormalizeLabel(value, MaximumTagNameScalars, "Tag name", allowLineBreaks: false);

    public static string? NormalizeDraftCustomTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Length == 0) return null;
        if (normalized.EnumerateRunes().Count() > MaximumLabelScalars)
        {
            throw new LibraryValidationException($"Tab title exceeds {MaximumLabelScalars} Unicode characters.");
        }
        if (normalized.Any(char.IsControl))
        {
            throw new LibraryValidationException("Tab title contains control characters.");
        }
        return normalized;
    }

    public static string ComparisonKey(string value) => value.Normalize(NormalizationForm.FormC).ToUpperInvariant();

    public static string NormalizeConnectionBaseUrl(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        if (trimmed.Length == 0) throw new LibraryValidationException("Base URL is required.");
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new LibraryValidationException("Base URL must be an absolute http or https address.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new LibraryValidationException("Base URL cannot contain an embedded username or password.");
        }

        if (uri.Scheme == "http" && !IsLoopbackOrPrivateHost(uri.Host))
        {
            throw new LibraryValidationException("HTTP base URLs are only permitted for loopback or private-network hosts.");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new LibraryValidationException("Base URL cannot contain a query string or fragment.");
        }

        var path = uri.AbsolutePath == "/" ? string.Empty : uri.AbsolutePath.TrimEnd('/');
        return $"{uri.Scheme}://{uri.Authority}{path}";
    }

    private static bool IsLoopbackOrPrivateHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!System.Net.IPAddress.TryParse(host, out var address)) return false;
        if (System.Net.IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168);
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
    }

    public static string ValidateMetadataValue(MetadataValueKind kind, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Encoding.UTF8.GetByteCount(value) > MaximumMetadataValueUtf8Bytes)
        {
            throw new LibraryValidationException("Metadata value exceeds the 1 MiB UTF-8 safety bound.");
        }

        switch (kind)
        {
            case MetadataValueKind.Number:
                if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    throw new LibraryValidationException("Number metadata must contain a finite invariant number.");
                }
                break;
            case MetadataValueKind.Boolean:
                if (!bool.TryParse(value, out _))
                {
                    throw new LibraryValidationException("Boolean metadata must be true or false.");
                }
                break;
            case MetadataValueKind.Date:
                if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    throw new LibraryValidationException("Date metadata must use YYYY-MM-DD.");
                }
                break;
            case MetadataValueKind.DateTime:
                if (!HasExplicitOffset(value) || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                {
                    throw new LibraryValidationException("Date-time metadata requires an explicit UTC offset.");
                }
                break;
            case MetadataValueKind.Json:
                ValidateJson(value);
                break;
        }

        return value;
    }

    public static UserMetadataFilter ValidateMetadataFilter(UserMetadataFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var key = NormalizeMetadataKey(filter.Key);
        var allowed = filter.Kind switch
        {
            MetadataValueKind.Text => filter.Operator is MetadataFilterOperator.Equals or MetadataFilterOperator.DoesNotEqual or MetadataFilterOperator.Contains,
            MetadataValueKind.Number or MetadataValueKind.Date or MetadataValueKind.DateTime => filter.Operator is MetadataFilterOperator.Equals or MetadataFilterOperator.DoesNotEqual or MetadataFilterOperator.LessThan or MetadataFilterOperator.LessThanOrEqual or MetadataFilterOperator.GreaterThan or MetadataFilterOperator.GreaterThanOrEqual,
            MetadataValueKind.Boolean => filter.Operator is MetadataFilterOperator.Equals or MetadataFilterOperator.DoesNotEqual,
            MetadataValueKind.Json => filter.Operator is MetadataFilterOperator.Exists or MetadataFilterOperator.DoesNotExist or MetadataFilterOperator.StructurallyEquals or MetadataFilterOperator.DoesNotEqual,
            _ => false
        };
        if (!allowed) throw new LibraryValidationException($"{filter.Operator} is not valid for {filter.Kind} metadata.");
        var needsValue = filter.Operator is not MetadataFilterOperator.Exists and not MetadataFilterOperator.DoesNotExist;
        if (needsValue && filter.ComparisonValue is null) throw new LibraryValidationException("The metadata filter requires a comparison value.");
        var value = needsValue ? ValidateMetadataValue(filter.Kind, filter.ComparisonValue!) : null;
        return filter with { Key = key, ComparisonValue = value };
    }

    private static void ValidateJson(string value)
    {
        var options = new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        };
        try
        {
            using var document = JsonDocument.Parse(value, options);
            ValidateJsonElement(document.RootElement, 0, new JsonNodeCounter());
        }
        catch (JsonException exception)
        {
            var line = exception.LineNumber is { } lineNumber ? lineNumber + 1 : 1;
            var column = exception.BytePositionInLine is { } bytePosition ? bytePosition + 1 : 1;
            throw new LibraryValidationException($"JSON metadata is invalid at line {line}, column {column}.");
        }
    }

    private static void ValidateJsonElement(JsonElement element, int depth, JsonNodeCounter counter)
    {
        if (depth > 32 || ++counter.Count > 100_000)
        {
            throw new LibraryValidationException("JSON metadata exceeds its structural safety bounds.");
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new LibraryValidationException("JSON metadata contains a duplicate property name.");
                }
                ValidateJsonElement(property.Value, depth + 1, counter);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                ValidateJsonElement(child, depth + 1, counter);
            }
        }
    }

    private static string NormalizeLabel(string value, int maximumScalars, string fieldName, bool allowLineBreaks)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = value.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Length == 0)
        {
            throw new LibraryValidationException($"{fieldName} is required.");
        }

        if (normalized.EnumerateRunes().Count() > maximumScalars)
        {
            throw new LibraryValidationException($"{fieldName} exceeds {maximumScalars} Unicode characters.");
        }

        if (normalized.Any(character => char.IsControl(character) && (!allowLineBreaks || character is not ('\r' or '\n'))))
        {
            throw new LibraryValidationException($"{fieldName} contains control characters.");
        }

        if (!allowLineBreaks && normalized.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new LibraryValidationException($"{fieldName} cannot contain line breaks.");
        }

        return normalized;
    }

    private static bool HasExplicitOffset(string value)
    {
        if (value.EndsWith('Z')) return true;
        if (value.Length < 6) return false;
        var offset = value.AsSpan(value.Length - 6);
        return (offset[0] is '+' or '-') && offset[3] == ':' &&
               char.IsAsciiDigit(offset[1]) && char.IsAsciiDigit(offset[2]) &&
               char.IsAsciiDigit(offset[4]) && char.IsAsciiDigit(offset[5]);
    }

    private sealed class JsonNodeCounter
    {
        public int Count { get; set; }
    }
}
