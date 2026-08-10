using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mellow.SlopFactory.Domain;

public static class LibraryRules
{
    public const string FormatIdentity = "mellow.slopfactory.library";
    public const int ManifestVersion = 1;
    public const int SchemaVersion = 18;
    public const int MaximumDisplayNameScalars = 255;
    public const int MaximumMetadataKeyScalars = 100;
    public const int MaximumLinkLabelScalars = 200;
    public const int MaximumLabelScalars = 100;
    public const int MaximumMetadataEntriesPerFile = 1_000;
    public const int MaximumMetadataValueUtf8Bytes = 1_048_576;
    public const int MaximumEditableTextUtf8Bytes = 4_194_304;
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
