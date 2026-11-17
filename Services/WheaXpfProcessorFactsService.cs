using System.Globalization;
namespace XinSpect;

/// <summary>Safe saved XPF evidence from the caller's existing single WHEA enumeration/publication lease.</summary>
public static class WheaXpfProcessorFactsService
{
    public const string SummaryKey = "whea.xpf.summary";
    public const string BoundaryKey = "whea.xpf.boundary";
    public const string RecordPrefix = "whea.xpf.rec.";
    public const int MaxListed = 16;
    private const string Source = "Saved CPER XPF section; SDK10.0.28000.0 cper.h519-962/cperguid.h120,199-216; Microsoft Learn WHEA_XPF_CONTEXT_INFO 16-byte zero padding";

    public static IReadOnlyList<HardwareFact> Collect(IReadOnlyList<WheaMemoryRecord> records, DateTimeOffset at)
    {
        var facts = new List<HardwareFact>();
        var seen = new HashSet<(ulong Event, int Section)>();
        int sections = 0, decoded = 0, rejected = 0, listed = 0;
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var record in records.OrderByDescending(r => r.RecordedAt))
        {
            if (record.Envelope is not { Valid: true, Cper: { } cper } envelope) continue;
            foreach (var section in cper.TypedSections.Where(s => s.SectionType == WheaXpfProcessorSectionDecoder.SectionType))
            {
                if (ulong.TryParse(envelope.EventIdentity, NumberStyles.None, CultureInfo.InvariantCulture, out var identity)
                    && !seen.Add((identity, section.DescriptorIndex))) continue;
                sections++;
                var parsed = WheaXpfProcessorSectionDecoder.Parse(section.Bytes, out var why);
                if (parsed is null) { rejected++; reasons.Add(why!); continue; }
                decoded++;
                if (listed == MaxListed) continue;
                listed++;
                // Ordinal keys deliberately omit event/hardware identity. Deduplication alone uses numeric EventRecordID internally.
                string key = RecordPrefix + "i" + listed.ToString("0000", CultureInfo.InvariantCulture);
                bool understood = parsed.Checks.Count > 0 && parsed.Checks.All(c => c.Kind != WheaXpfCheckKind.Unknown && c.CheckInfoValid);
                var fields = new List<string> { $"RecordedAt {record.RecordedAt.ToUniversalTime():O}",
                    $"RecordSeverity {section.RecordSeverity}; SectionSeverity {section.SectionSeverity}",
                    envelope.EventLevel is { } level ? $"EventLevel {level} (not CPER severity)" : "EventLevel unavailable",
                    $"LocalAPICId validity {parsed.LocalApicIdValid}; CpuId validity {parsed.CpuIdValid}; identity values omitted",
                    $"Processor information {parsed.Checks.Count}; contexts {parsed.Contexts.Count}" };
                for (int i = 0; i < parsed.Checks.Count; i++)
                {
                    var check = parsed.Checks[i];
                    string prefix = $"Check {i}: {check.Kind}; CheckInfoValid {check.CheckInfoValid}";
                    var detail = new List<string>();
                    Add(detail, "TransactionType", check.TransactionType); Add(detail, "Operation", check.Operation);
                    Add(detail, "Level", check.Level); Add(detail, "MicroarchitectureErrorType", check.ErrorType);
                    Add(detail, "ProcessorContextCorrupt", check.ProcessorContextCorrupt); Add(detail, "Uncorrected", check.Uncorrected);
                    Add(detail, "PreciseIP", check.PreciseIp); Add(detail, "RestartableIP", check.RestartableIp);
                    Add(detail, "Overflow", check.Overflow); Add(detail, "Participation", check.Participation);
                    Add(detail, "Timeout", check.Timeout); Add(detail, "AddressSpace", check.AddressSpace);
                    fields.Add(prefix + (detail.Count == 0 ? "; no supported valid fields" : "; " + string.Join(", ", detail)));
                }
                for (int i = 0; i < parsed.Contexts.Count; i++)
                    fields.Add($"Context {i}: type {parsed.Contexts[i].Type} (raw metadata only); data bytes {parsed.Contexts[i].DataBytes}; payload omitted");
                facts.Add(new(key, WheaMemoryFactsService.Category, $"WHEA x86/x64 processor section {listed}",
                    string.Join("; ", fields), "", Source, FactTrustLevel.Reported, false, at, null,
                    understood ? FactAvailability.Present : FactAvailability.Unknown,
                    understood ? null : "Some check kinds/validity are unavailable; context metadata is structural only."));
            }
        }
        FactAvailability availability = sections > 0 && rejected == 0 ? FactAvailability.Present : FactAvailability.Unknown;
        string? reason = sections == 0 ? "No validated saved XPF section evidence; absence does not establish absence of processor errors."
            : rejected > 0 ? "Some XPF payloads were rejected; decoded evidence is partial." : null;
        string value = $"Retained candidate events {records.Count}; XPF section descriptors {sections}; decoded {decoded}; rejected {rejected}; listed {listed} (cap {MaxListed}).";
        if (reasons.Count > 0) value += " Rejection: " + string.Join("; ", reasons);
        if (records is WheaMemoryReadResult read)
        {
            value += $" Window {read.WindowStartUtc:O} through {read.WindowEndUtc:O}; enumeration {read.Status}.";
            if (read.Status != WheaMemoryReadStatus.Complete)
            {
                availability = read.Status switch { WheaMemoryReadStatus.InsufficientPrivilege => FactAvailability.InsufficientPrivilege,
                    WheaMemoryReadStatus.Truncated => FactAvailability.Unknown, _ => FactAvailability.ReadError };
                reason = "Enumeration incomplete; retained XPF section counts are lower bounds.";
            }
        }
        else { availability = FactAvailability.Unknown; reason = "Injected list has no source completeness contract; retained section count only."; }
        facts.Insert(0, new(SummaryKey, WheaMemoryFactsService.Category, "WHEA x86/x64 processor summary", value, "sections", Source,
            FactTrustLevel.Reported, false, at, availability == FactAvailability.Present ? sections : null, availability, reason));
        facts.Add(new(BoundaryKey, WheaMemoryFactsService.Category, "WHEA x86/x64 processor evidence boundary",
            "Saved XPF section check fields only; all scalar data requires both PROCINFO.CheckInfo and its own validity bit. Numeric enum codes are raw, not guessed labels. Context type/length are metadata, not validated register semantics. CPU/APIC/event identities, addresses, unknown GUIDs and register payloads are omitted. No live diagnosis, CPU/socket attribution, native MCA bank decoding or real-event compatibility claim. Synthetic fixtures do not establish hardware support.",
            "", "Project scope", FactTrustLevel.Reported, false, at));
        return facts;
    }

    private static void Add<T>(List<string> parts, string name, T? value) where T : struct
    { if (value.HasValue) parts.Add(name + " " + value.Value); }
}
