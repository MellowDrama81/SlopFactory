using System.Text;
using System.Text.Json;

namespace Mellow.SlopFactory.Domain;

/// <summary>
/// Distinguishes a provider response that failed the expected-media-category check because it's a
/// recognizable rejection (an HTML error/authentication page, or a JSON error document) from bytes
/// that are genuinely unrecognized. If the non-empty bytes are not recognized as an
/// error document, authentication page or provider-blocked payload, the result review offers Retain
/// as Unverified Binary or Discard." Recognized rejections are never eligible for retention — only
/// the unrecognized remainder is.
/// </summary>
public static class ProviderRejectionPayloadClassifier
{
    /// <summary>Real provider error/authentication pages are small; bytes beyond this are never
    /// inspected as text, so a genuine (if mistyped) media payload isn't wastefully decoded.</summary>
    private const int MaxCandidateBytesForTextInspection = 65_536;

    private static readonly char[] LeadingWhitespaceAndBom = ['﻿', ' ', '\t', '\r', '\n'];

    public static bool IsRecognizedRejectionPayload(byte[] bytes, string detectedMediaType)
    {
        if (bytes.Length == 0 || bytes.Length > MaxCandidateBytesForTextInspection) return false;
        if (detectedMediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            || detectedMediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)
            || LooksLikeHtml(bytes))
        {
            return true;
        }
        return LooksLikeJsonErrorDocument(bytes);
    }

    private static bool LooksLikeHtml(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).TrimStart(LeadingWhitespaceAndBom);
        return text.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeJsonErrorDocument(byte[] bytes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            return document.RootElement.ValueKind == JsonValueKind.Object
                && (document.RootElement.TryGetProperty("error", out _) || document.RootElement.TryGetProperty("errors", out _));
        }
    }
}
