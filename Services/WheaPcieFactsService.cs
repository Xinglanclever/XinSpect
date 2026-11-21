using System.Globalization;
namespace XinSpect;

/// <summary>Saved-event PCIe evidence from the same accepted WHEA enumeration as memory facts.</summary>
public static class WheaPcieFactsService
{
    public const string SummaryKey = "whea.pcie.summary";
    public const string BoundaryKey = "whea.pcie.boundary";
    public const string RecordPrefix = "whea.pcie.rec.";
    public const int MaxListed = 16;
    private const string Source = "WHEA-Logger saved CPER PCIe section; SDK10.0.28000.0 cper.h (208 bytes)";
    public static IReadOnlyList<HardwareFact> Collect(IReadOnlyList<WheaMemoryRecord> records, DateTimeOffset at)
    {
        var facts = new List<HardwareFact>();
        int sections = 0, decoded = 0, rejected = 0, listed = 0;
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records.OrderByDescending(r => r.RecordedAt))
        {
            if (record.Envelope is not { Valid: true, Cper: { } cper } envelope) continue;
            foreach (var section in cper.TypedSections.Where(s => s.SectionType == CperMemoryEnvelopeDecoder.PcieType))
            {
                // A retained event may contain multiple PCIe sections; deduplicate the compound identity only.
                string? stableIdentity = ulong.TryParse(envelope.EventIdentity, NumberStyles.None, CultureInfo.InvariantCulture, out var stableId)
                    ? "e" + stableId.ToString(CultureInfo.InvariantCulture) + ".s" + section.DescriptorIndex.ToString(CultureInfo.InvariantCulture) : null;
                if (stableIdentity is not null && !seen.Add(stableIdentity)) continue;
                sections++;
                var parsed = WheaPcieSectionDecoder.Parse(section.Bytes, out var why);
                if (parsed is null) { rejected++; reasons.Add(why!); continue; }
                decoded++;
                if (listed == MaxListed) continue;
                listed++;
                // Accepted production envelopes supply numeric EventRecordID; never copy arbitrary injected identity into keys.
                string identity = stableIdentity ?? "i" + listed.ToString("0000", CultureInfo.InvariantCulture) + ".s" + section.DescriptorIndex.ToString(CultureInfo.InvariantCulture);
                var fields = new List<string> { $"{record.RecordedAt.ToUniversalTime():O} EventID {record.EventId}",
                    $"RecordSeverity {section.RecordSeverity}", $"SectionSeverity {section.SectionSeverity}", $"ValidBits 0x{parsed.ValidBits:X16}" };
                fields.Add(envelope.EventLevel is { } level ? $"EventLevel {level} (Windows event level, not CPER severity)" : "EventLevel unavailable");
                if (parsed.PortType is { } port) fields.Add($"PortType {port} ({(port <= int.MaxValue ? WheaSectionKnowledge.PciePortName((int)port) : null) ?? "unassigned/unknown"})");
                if (parsed.MajorVersion is { } major) fields.Add($"PCIe version {major:X2}.{parsed.MinorVersion!.Value:X2} (SDK raw version bytes)");
                if (parsed.Command is { } command) fields.Add($"Command 0x{command:X4}; Status 0x{parsed.Status!.Value:X4}");
                if (parsed.DeviceId is { } id)
                    fields.Add($"Vendor 0x{id.VendorId:X4}; Device 0x{id.DeviceId:X4}; Class 0x{id.ClassCode:X6}; logical segment:primary-bus:device.function {id.Segment:X4}:{id.PrimaryBus:X2}:{id.Device:X2}.{id.Function}; SecondaryBus {id.SecondaryBus:X2}; reported Slot {id.Slot}");
                if (parsed.DeviceIdRejection is { } identityWhy) fields.Add(identityWhy);
                if (parsed.BridgeControl is { } bridge) fields.Add($"BridgeControl 0x{bridge:X4}; BridgeSecondaryStatus 0x{parsed.BridgeSecondaryStatus!.Value:X4}");
                fields.Add(parsed.HasExpressCapability ? "ExpressCapability valid: 60 raw bytes; contents not published/decoded" : "ExpressCapability not valid");
                fields.Add(parsed.HasAerInfo ? "AerInfo valid: 96 raw bytes; contents not published/decoded" : "AerInfo not valid");
                fields.Add("Saved logical evidence only; no current PnP/physical-card/CPU/PCH attribution");
                facts.Add(new(RecordPrefix + identity, WheaMemoryFactsService.Category, $"WHEA PCIe record {listed}",
                    string.Join("; ", fields), "", Source, FactTrustLevel.Reported, false, at));
                if (parsed.DeviceSerialNumber is { } serial)
                    facts.Add(new(RecordPrefix + identity + ".serial", WheaMemoryFactsService.Category, $"WHEA PCIe serial {listed}",
                        $"0x{serial:X16}", "", Source, FactTrustLevel.Reported, true, at));
            }
        }
        FactAvailability availability = sections == 0 ? FactAvailability.Unknown : FactAvailability.Present;
        int invalid = records.Count(r => r.Envelope is { Valid: false });
        int missing = records.Count(r => r.Envelope is null);
        string value = $"Retained candidate events {records.Count}; rejected envelopes {invalid}; no verified envelope {missing}; validated PCIe sections {sections}; decoded {decoded}; rejected {rejected}; listed {listed} (cap {MaxListed}).";
        string? reason = sections == 0 ? "No validated PCIe section evidence; absence of retained records does not establish absence of PCIe errors." : null;
        if (reasons.Count > 0) value += " Rejection: " + string.Join("; ", reasons.OrderBy(r => r, StringComparer.Ordinal));
        if (records is WheaMemoryReadResult read)
        {
            value += $" Window {read.WindowStartUtc:O} through {read.WindowEndUtc:O}; enumeration {read.Status}.";
            if (read.Status != WheaMemoryReadStatus.Complete)
            {
                availability = read.Status switch { WheaMemoryReadStatus.InsufficientPrivilege => FactAvailability.InsufficientPrivilege,
                    WheaMemoryReadStatus.Truncated => FactAvailability.Unknown, _ => FactAvailability.ReadError };
                // Do not copy injected arbitrary reasons or payloads into non-sensitive summary facts.
                reason = "Enumeration incomplete; retained events and sections are a lower bound, not a complete total.";
            }
        }
        else { availability = FactAvailability.Unknown; reason = "Injected list has no source completeness contract; retained section count only."; }
        facts.Insert(0, new(SummaryKey, WheaMemoryFactsService.Category, "WHEA PCIe summary", value, "sections", Source,
            FactTrustLevel.Reported, false, at, availability == FactAvailability.Present ? sections : null, availability, reason));
        facts.Add(new(BoundaryKey, WheaMemoryFactsService.Category, "WHEA PCIe evidence boundary",
            "Validity bits gate every field. Event ID is a candidate selector, not PCIe classification. Record and section severities are distinct. BDF is saved logical evidence; no physical attribution or device replacement diagnosis. Serial is a separate sensitive fact. Express/AER contents and extension bytes are not published or interpreted. Synthetic fixtures do not establish hardware compatibility.",
            "", "Project scope", FactTrustLevel.Reported, false, at));
        return facts;
    }
}
