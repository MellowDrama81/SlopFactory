using System.Text;
using System.Xml;
using System.Xml.Linq;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Infrastructure.Storage;

internal static class SvgSanitizer
{
    private static readonly XNamespace SvgNamespace = "http://www.w3.org/2000/svg";
    private static readonly HashSet<string> AllowedElements = new(StringComparer.Ordinal)
    {
        "svg", "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "text", "tspan",
        "defs", "linearGradient", "radialGradient", "stop", "clipPath", "mask", "pattern", "marker", "title", "desc", "symbol", "use"
    };

    public static byte[] Sanitize(byte[] source)
    {
        using var input = new MemoryStream(source, writable: false);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = LibraryRules.MaximumInlineImageBytes,
            IgnoreProcessingInstructions = true
        };
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(input, settings);
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException exception)
        {
            throw new LibraryValidationException($"The SVG is invalid: {exception.Message}");
        }

        if (document.Root is null || document.Root.Name != SvgNamespace + "svg")
        {
            throw new LibraryValidationException("The file does not contain a supported SVG root element.");
        }

        foreach (var node in document.DescendantNodes().OfType<XComment>().ToArray()) node.Remove();
        foreach (var element in document.Root.DescendantsAndSelf().Reverse().ToArray())
        {
            if (element.Name.Namespace != SvgNamespace || !AllowedElements.Contains(element.Name.LocalName))
            {
                element.Remove();
                continue;
            }
            foreach (var attribute in element.Attributes().ToArray())
            {
                var localName = attribute.Name.LocalName;
                var value = attribute.Value.Trim();
                if (localName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || localName.Equals("style", StringComparison.OrdinalIgnoreCase)
                    || (localName.Equals("href", StringComparison.OrdinalIgnoreCase) && !value.StartsWith('#'))
                    || ContainsUnsafeUrl(value)
                    || (!attribute.IsNamespaceDeclaration && attribute.Name.Namespace != XNamespace.None && attribute.Name.Namespace != XNamespace.Xml))
                {
                    attribute.Remove();
                }
            }
        }

        document.Declaration = null;
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = true, Indent = false }))
        {
            document.Save(writer);
        }
        return output.ToArray();
    }

    private static bool ContainsUnsafeUrl(string value)
    {
        var remaining = value;
        while (true)
        {
            var index = remaining.IndexOf("url(", StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            var rest = remaining[(index + 4)..].TrimStart();
            if (!rest.StartsWith('#')) return true;
            var close = rest.IndexOf(')');
            if (close < 0) return true;
            remaining = rest[(close + 1)..];
        }
    }
}
