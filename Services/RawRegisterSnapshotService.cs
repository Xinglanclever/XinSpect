using System.Linq;

namespace XinSpect;

/// <summary>
/// 一段來源的原始暫存器位元組（北極星：全機逐位元組快照的最小單位）。
/// 讀不到就 Bytes=null 並標 Availability+原因，絕不以 0 填補。VolatilityMask 標出已知會每次變動的位元組
/// （計數器、TSC、溫度 live 位…），差分時略過但仍保存，避免「每次快照都在變」淹沒真正的變動。
/// </summary>
public sealed record RawRegisterRegion
{
    public required string Source { get; init; }           // 例："msr:0x198@cpu3"、"pcicfg:00:1f.0"、"acpi:BERT"、"smbios"
    public byte[]? Bytes { get; init; }
    public FactAvailability Availability { get; init; } = FactAvailability.Present;
    public string? UnavailableReason { get; init; }
    public byte[]? VolatilityMask { get; init; }           // 與 Bytes 同長；非 0 = 該位元組易變，差分略過
}

public enum RawRegionChangeKind { Added, Removed, Changed, Unchanged, AvailabilityChanged }

/// <summary>單一來源在兩份快照間的差異。ChangedOffsets 只含「非遮罩且真的不同」的位元組位置。</summary>
public sealed record RawRegionChange
{
    public required string Source { get; init; }
    public RawRegionChangeKind Kind { get; init; }
    public IReadOnlyList<int> ChangedOffsets { get; init; } = [];
    public FactAvailability PreviousAvailability { get; init; }
    public FactAvailability CurrentAvailability { get; init; }
}

public sealed record RawRegisterDiff
{
    public required IReadOnlyList<RawRegionChange> Regions { get; init; }
    public int Added => Regions.Count(r => r.Kind == RawRegionChangeKind.Added);
    public int Removed => Regions.Count(r => r.Kind == RawRegionChangeKind.Removed);
    public int Changed => Regions.Count(r => r.Kind == RawRegionChangeKind.Changed);
    public int Unchanged => Regions.Count(r => r.Kind == RawRegionChangeKind.Unchanged);
    public int AvailabilityChanged => Regions.Count(r => r.Kind == RawRegionChangeKind.AvailabilityChanged);
}

/// <summary>原始暫存器快照的差分器（純邏輯，可完整單測）。來源以 Source 字串配對，逐位元組比對並套用揮發遮罩。</summary>
public static class RawRegisterSnapshotService
{
    public static RawRegisterDiff Diff(IReadOnlyList<RawRegisterRegion> before, IReadOnlyList<RawRegisterRegion> after)
    {
        var left = before.ToDictionary(r => r.Source, StringComparer.Ordinal);
        var right = after.ToDictionary(r => r.Source, StringComparer.Ordinal);
        var sources = left.Keys.Concat(right.Keys).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);
        var changes = new List<RawRegionChange>();
        foreach (var src in sources)
        {
            left.TryGetValue(src, out var b);
            right.TryGetValue(src, out var a);
            if (b is null)
            {
                changes.Add(new() { Source = src, Kind = RawRegionChangeKind.Added, CurrentAvailability = a!.Availability });
                continue;
            }
            if (a is null)
            {
                changes.Add(new() { Source = src, Kind = RawRegionChangeKind.Removed, PreviousAvailability = b.Availability });
                continue;
            }
            if (b.Availability != a.Availability)
            {
                changes.Add(new()
                {
                    Source = src,
                    Kind = RawRegionChangeKind.AvailabilityChanged,
                    PreviousAvailability = b.Availability,
                    CurrentAvailability = a.Availability,
                });
                continue;
            }
            if (a.Availability != FactAvailability.Present)
            {
                // 兩次都讀不到且狀態相同 → 視為未變。
                changes.Add(new()
                {
                    Source = src,
                    Kind = RawRegionChangeKind.Unchanged,
                    PreviousAvailability = b.Availability,
                    CurrentAvailability = a.Availability,
                });
                continue;
            }
            var offsets = DiffBytes(b.Bytes ?? [], a.Bytes ?? [], a.VolatilityMask ?? b.VolatilityMask);
            changes.Add(new()
            {
                Source = src,
                Kind = offsets.Count == 0 ? RawRegionChangeKind.Unchanged : RawRegionChangeKind.Changed,
                ChangedOffsets = offsets,
                PreviousAvailability = FactAvailability.Present,
                CurrentAvailability = FactAvailability.Present,
            });
        }
        return new RawRegisterDiff { Regions = changes };
    }

    /// <summary>逐位元組比對，套用揮發遮罩（mask[i] != 0 的位元組略過）。長度不同的尾段視為變動（除非遮罩略過）。</summary>
    private static List<int> DiffBytes(byte[] before, byte[] after, byte[]? mask)
    {
        var offsets = new List<int>();
        int max = System.Math.Max(before.Length, after.Length);
        for (int i = 0; i < max; i++)
        {
            if (mask is not null && i < mask.Length && mask[i] != 0) continue;
            bool bothHave = i < before.Length && i < after.Length;
            byte bv = i < before.Length ? before[i] : (byte)0;
            byte av = i < after.Length ? after[i] : (byte)0;
            if (!bothHave || bv != av) offsets.Add(i);
        }
        return offsets;
    }
}
