using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;
namespace XinSpect;

public sealed record WheaEventEnvelope(string? EventIdentity, CperMemoryEnvelope? Cper, string? Reason)
{
    public byte? EventLevel { get; init; }
    public bool Valid => Reason is null && Cper?.Valid == true;
}

/// <summary>Installed-provider manifest contract, not a hardware fixture or universal Windows schema.</summary>
public static class WheaEventEnvelopeParser
{
    public const int MaxXmlChars = CperMemoryEnvelopeDecoder.MaxBytes * 2 + 65536;
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events";
    private static readonly Guid Provider = new("c26c4f3c-3f66-4e99-8f8a-39405cfed220");
    public static WheaEventEnvelope Parse(string xml, int expectedEventId)
    {
        WheaEventEnvelope Reject(string reason) => new(null, null, reason);
        if (xml.Length > MaxXmlChars) return Reject("Event XML exceeds character budget.");
        try
        {
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaxXmlChars, MaxCharactersFromEntities = 0,
            });
            var doc = XDocument.Load(reader, LoadOptions.None);
            var root = doc.Root;
            if (root?.Name != Ns + "Event") return Reject("Event XML namespace/root rejected.");
            XElement? One(XElement parent, string name)
            {
                var matches = parent.Elements(Ns + name).Take(2).ToArray();
                return matches.Length == 1 ? matches[0] : null;
            }
            var sys = One(root, "System"); var data = One(root, "EventData");
            if (sys is null || data is null) return Reject("Event System/EventData must be unique.");
            var provider = One(sys, "Provider");
            if (!Guid.TryParse((string?)provider?.Attribute("Guid"), out var guid) || guid != Provider)
                return Reject("Event provider rejected.");
            if (!int.TryParse(One(sys, "EventID")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int id)
                || id != expectedEventId) return Reject("Event ID rejected.");
            if (!int.TryParse(One(sys, "Version")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int version)
                || version != 0 || id is not (1 or 17 or 18 or 19 or 23 or 46 or 47))
                return Reject("Event ID/version has no installed RawData contract.");
            if (!ulong.TryParse(One(sys, "EventRecordID")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var recordId))
                return Reject("EventRecordID unavailable; stable section identity cannot be established.");
            byte? eventLevel = null;
            var levels = sys.Elements(Ns + "Level").Take(2).ToArray();
            if (levels.Length > 1 || (levels.Length == 1 &&
                (levels[0].HasElements || !byte.TryParse(levels[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out _))))
                return Reject("Event Level must be a unique scalar byte when present.");
            if (levels.Length == 1) eventLevel = byte.Parse(levels[0].Value, CultureInfo.InvariantCulture);
            var fields = data.Elements(Ns + "Data").ToArray();
            var lengths = fields.Where(x => (string?)x.Attribute("Name") == "Length").Take(2).ToArray();
            var raws = fields.Where(x => (string?)x.Attribute("Name") == "RawData").Take(2).ToArray();
            if (lengths.Length != 1 || raws.Length != 1 || lengths[0].HasElements || raws[0].HasElements)
                return Reject("Length/RawData must be unique scalar named fields.");
            if (!uint.TryParse(lengths[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out uint length)
                || length > CperMemoryEnvelopeDecoder.MaxBytes || length < 128)
                return Reject("Event binary length rejected.");
            string hex = raws[0].Value;
            if (hex.Length != checked((int)length * 2) || hex.Any(c => !Uri.IsHexDigit(c)))
                return Reject("RawData must be exact-length hexadecimal without separators.");
            byte[] bytes = Convert.FromHexString(hex);
            var cper = CperMemoryEnvelopeDecoder.Parse(bytes);
            // Only bounded memory/PCIe/Processor Generic slices survive; each decoder validates its payload. Raw XML/full RawData are never public facts.
            return new(recordId.ToString(CultureInfo.InvariantCulture), cper, cper.Reason) { EventLevel = eventLevel };
        }
        catch (Exception ex) when (ex is XmlException or FormatException or OverflowException or ArgumentException)
        {
            return Reject("Event envelope rejected (" + ex.GetType().Name + ").");
        }
    }
}
