using System.Globalization;
namespace XinSpect;

/// <summary>Saved-event processor generic evidence from the same accepted WHEA enumeration as memory facts.</summary>
public static class WheaProcessorGenericFactsService
{
    public const string SummaryKey = "whea.proc.summary";
    public const string BoundaryKey = "whea.proc.boundary";
    public const string RecordPrefix = "whea.proc.rec.";
    public const int MaxListed = 16;
    private const string Source = "WHEA-Logger saved CPER processor generic section; SDK10.0.28000.0 cper.h (192 bytes)";
    public static IReadOnlyList<HardwareFact> Collect(IReadOnlyList<WheaMemoryRecord> records, DateTimeOffset at)
    {
        var facts = new List<HardwareFact>();
        int sections = 0, decoded = 0, rejected = 0, listed = 0;
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records.OrderByDescending(r => r.RecordedAt))
        {
            if (record.Envelope is not { Valid: true, Cper: { } cper } envelope) continue;
            foreach (var section in cper.TypedSections.Where(s => s.SectionType == CperMemoryEnvelopeDecoder.ProcessorGenericType))
            {
                // A retained event may contain multiple processor generic sections; deduplicate the compound identity only.
                string? stableIdentity = ulong.TryParse(envelope.EventIdentity, NumberStyles.None, CultureInfo.InvariantCulture, out var stableId)
                    ? "e" + stableId.ToString(CultureInfo.InvariantCulture) + ".s" + section.DescriptorIndex.ToString(CultureInfo.InvariantCulture) : null;
                if (stableIdentity is not null && !seen.Add(stableIdentity)) continue;
                sections++;
                var parsed = WheaProcessorGenericSectionDecoder.Parse(section.Bytes, out var why);
                if (parsed is null) { rejected++; reasons.Add(why!); continue; }
                decoded++;
                if (listed == MaxListed) continue;
                listed++;
                // Accepted production envelopes supply numeric EventRecordID; never copy arbitrary injected identity into keys.
                string identity = stableIdentity ?? "i" + listed.ToString("0000", CultureInfo.InvariantCulture) + ".s" + section.DescriptorIndex.ToString(CultureInfo.InvariantCulture);
                var fields = new List<string> { $"{record.RecordedAt.ToUniversalTime():O} EventID {record.EventId}",
                    $"RecordSeverity {section.RecordSeverity}", $"SectionSeverity {section.SectionSeverity}", $"ValidBits 0x{parsed.ValidBits:X16}" };
                fields.Add(envelope.EventLevel is { } level ? $"EventLevel {level} (Windows event level, not CPER severity)" : "EventLevel unavailable");
                if(parsed.ProcessorType is {} type) fields.Add($"ProcessorType {type} ({type switch {0=>"x86/x64",1=>"IPF",2=>"ARM",_=>"unassigned/unknown"}})");
                if(parsed.InstructionSet is {} isa) fields.Add($"ISA {isa} ({isa switch {0=>"x86",1=>"IPF",2=>"x64",4=>"ARM32",8=>"ARM64",_=>"unassigned/unknown"}})");
                if(parsed.ErrorType is {} error) fields.Add($"ErrorType 0x{error:X2} ({error switch {0=>"unknown",1=>"cache",2=>"TLB",4=>"bus",8=>"microarchitectural",_=>"combination/unassigned; raw retained"}})");
                if(parsed.Operation is {} op) fields.Add($"Operation {op} ({op switch {0=>"generic",1=>"data read",2=>"data write",3=>"instruction execution",_=>"unassigned/unknown"}})");
                if(parsed.Flags is {} flags) fields.Add($"Flags 0x{flags:X2}; restartable={(flags&1)!=0}; precise-IP={(flags&2)!=0}; overflow={(flags&4)!=0}; hardware/firmware-reported corrected={(flags&8)!=0}; unknown bits 0x{flags&0xf0:X2}");
                if(parsed.Level is {} levelValue) fields.Add($"Structure level {levelValue} (0 is lowest cache level)");
                if(parsed.CpuVersion is {} version) fields.Add($"CPUVersion low32 raw 0x{version:X8} (bounded legacy family field; high32/NativeModelId not published)");
                if(parsed.ProcessorId is {} processor) fields.Add($"ProcessorId raw 0x{processor:X16} (x86/x64 local APIC ID; IPF LID; no Windows CPU/socket attribution)");
                fields.Add("Brand/address payloads omitted; saved evidence only, not a live CPU diagnosis");
                facts.Add(new(RecordPrefix + identity, WheaMemoryFactsService.Category, $"WHEA processor generic record {listed}",
                    string.Join("; ", fields), "", Source, FactTrustLevel.Reported, false, at));
            }
        }
        FactAvailability availability = sections == 0 ? FactAvailability.Unknown : FactAvailability.Present;
        int invalid = records.Count(r => r.Envelope is { Valid: false });
        int missing = records.Count(r => r.Envelope is null);
        string value = $"Retained candidate events {records.Count}; rejected envelopes {invalid}; no verified envelope {missing}; validated processor generic sections {sections}; decoded {decoded}; rejected {rejected}; listed {listed} (cap {MaxListed}).";
        string? reason = sections == 0 ? "No validated processor generic section evidence; absence of retained records does not establish absence of processor generic errors." : null;
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
        facts.Insert(0, new(SummaryKey, WheaMemoryFactsService.Category, "WHEA processor generic summary", value, "sections", Source,
            FactTrustLevel.Reported, false, at, availability == FactAvailability.Present ? sections : null, availability, reason));
        facts.Add(new(BoundaryKey, WheaMemoryFactsService.Category, "WHEA processor generic evidence boundary",
            "Validity bits gate fields. Event ID is only a candidate selector. ProcessorId is architecture-dependent, not a Windows logical CPU index or socket attribution. Record/section severity and event level differ. Brand/addresses and extension payloads are not retained or output. NativeModelId validity is unsupported. Synthetic fixtures do not establish real-event/hardware compatibility.",
            "", "Project scope", FactTrustLevel.Reported, false, at));
        return facts;
    }
}
