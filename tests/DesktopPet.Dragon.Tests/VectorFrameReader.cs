using System.IO;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace DesktopPet;

internal static class VectorFrameReader
{
    internal static DrawingImage Read(string path)
    {
        // Character SVGs are self-contained artwork, never network documents.
        using var xml = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 20_000_000 });
        var document = XDocument.Load(xml);
        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName is "image" or "script" or "foreignObject" or "style")
                throw new InvalidDataException("SVG must contain self-contained vector artwork.");
            foreach (var attribute in element.Attributes())
            {
                if (attribute.Name.LocalName == "href" && !attribute.Value.StartsWith('#'))
                    throw new InvalidDataException("External SVG references are not supported.");
                if (attribute.Value.Contains("url(", StringComparison.OrdinalIgnoreCase)
                    && !attribute.Value.Contains("url(#", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("External SVG paint references are not supported.");
            }
        }
        var settings = new WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = true };
        using var reader = new FileSvgReader(settings) { SaveXaml = false, SaveZaml = false };
        var drawing = reader.Read(path) ?? throw new InvalidDataException("SVG drawing is empty.");
        var image = new DrawingImage(drawing);
        if (image.CanFreeze) image.Freeze();
        return image;
    }
}
