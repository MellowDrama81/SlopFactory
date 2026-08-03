using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mellow.SlopFactory.Domain;

public static class LibraryRules
{
    public const string FormatIdentity = "mellow.slopfactory.library";
    public const int ManifestVersion = 1;
    public const int SchemaVersion = 4;
    public const int MaximumDisplayNameScalars = 255;
    public const int MaximumMetadataKeyScalars = 100;
    public const int MaximumLinkLabelScalars = 200;
    public const int MaximumMetadataEntriesPerFile = 1_000;
    public const int MaximumMetadataValueUtf8Bytes = 1_048_576;
    public const int MaximumEditableTextUtf8Bytes = 4_194_304;
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

    public static string ComparisonKey(string value) => value.Normalize(NormalizationForm.FormC).ToUpperInvariant();

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

    private static void ValidateJson(string value)
    {
        var options = new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        };
        using var document = JsonDocument.Parse(value, options);
        ValidateJsonElement(document.RootElement, 0, new JsonNodeCounter());
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
                    throw new LibraryValidationException($"JSON object contains duplicate property '{property.Name}'.");
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
