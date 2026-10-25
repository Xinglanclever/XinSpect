namespace XinSpect;

/// <summary>WMI Win32_PhysicalMemory 的一列（SPD 一致性稽核的第四來源視圖）。</summary>
/// <remarks>只攜帶對照需要的欄位；Capacity 以位元組為單位（WMI 原樣），對照時換算 MiB。</remarks>
public sealed record WmiPhysicalMemoryRow(
    string? DeviceLocator,
    string? Manufacturer,
    string? PartNumber,
    string? SerialNumber,
    long? CapacityBytes,
    int? SpeedMTs);

/// <summary>
/// U10-OS-0030（候選鍵 <c>wmi.mem.cross</c>；實際鍵 <see cref="MemWmiCrossKey"/>）：把 <c>Win32_PhysicalMemory</c> 納入 SPD 一致性審計的
/// <b>第四源對照</b>——既有三源為原生 SPD／CPU-Z／SMBIOS（見 <see cref="SpdConsistencyAuditService"/>），
/// WMI 這一面此前只有零散查詢（DimmReferenceService／SystemInfoService／PlatformGovernanceFactsService），
/// 從未與 SPD 解碼結果並列對帳。
///
/// <para>
/// <b>沿用既有列舉與解碼，不新增第二份讀取</b>：呼叫端把已由既有通路取得的
/// 原生 SPD（<see cref="SpdConsistencyAuditService.FromNative"/>）與 SMBIOS
/// （<see cref="SpdConsistencyAuditService.FromSmbios"/>）正規化輸入直接傳進來，
/// 本服務只補 WMI 這一面的讀取與對照——不重解 SPD、不重讀 SMBIOS。
/// </para>
///
/// <para>
/// 對照面：條數、容量、序號、料號。配對以序號為最強依據（沿用稽核服務的權重慣例），
/// 配不上就標「未配對」，<b>不以清單順序強配</b>；缺任何一源如實標，不猜。
/// 只列矛盾<b>計數</b>，不列序號或料號原值（遮蔽慣例）。
/// </para>
/// </summary>
public static class MemWmiCrossFactsService
{
    public const string MemWmiCrossKey = "mem.wmi.cross";

    private const string Category = "記憶體";

    /// <summary>對照統計。Conflicts 三欄只數「兩面都在且不同」，缺面不算矛盾。</summary>
    public sealed record MemWmiCrossState(
        int WmiModules, int NativeModules, int SmbiosModules,
        int ComparedModules, int SerialConflicts, int CapacityConflicts, int PartConflicts);

    public delegate IReadOnlyList<WmiPhysicalMemoryRow>? WmiProbe();

    public static HardwareFact Collect(DateTimeOffset at,
        IReadOnlyList<SpdAuditSourceInput>? native,
        IReadOnlyList<SpdAuditSourceInput>? smbios,
        WmiProbe? probe = null)
    {
        IReadOnlyList<WmiPhysicalMemoryRow>? rows;
        try { rows = probe is null ? ReadWmi() : probe(); } catch { rows = null; }
        if (rows is null)
            return new HardwareFact(MemWmiCrossKey, Category, "WMI 記憶體對照", "", "",
                "WMI Win32_PhysicalMemory（唯讀）", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError, "Win32_PhysicalMemory 列舉失敗——第四源讀不到就不猜");

        var state = Cross(rows, native, smbios);
        string missing =
            (state.NativeModules == 0 ? "原生 SPD " : "") +
            (state.SmbiosModules == 0 ? (state.NativeModules == 0 ? "、SMBIOS" : "SMBIOS") : "");
        string missingNote = missing.Length == 0 ? "四源齊備" : $"缺源：{missing}（如實標，不補值）";
        string detail =
            $"WMI {state.WmiModules} 條、原生 SPD {state.NativeModules} 條、SMBIOS {state.SmbiosModules} 條；" +
            $"可配對 {state.ComparedModules} 條，序號矛盾 {state.SerialConflicts}、容量矛盾 {state.CapacityConflicts}、料號矛盾 {state.PartConflicts}。" +
            $"{missingNote}。矛盾是差異，不等同於偽造；未配對的條不以順序強配。";
        return new HardwareFact(MemWmiCrossKey, Category, "WMI 記憶體對照", detail, "條",
            "WMI Win32_PhysicalMemory×既有 SPD 解碼（唯讀）", FactTrustLevel.Reported, false, at,
            state.WmiModules);
    }

    /// <summary>純對照：WMI 列 × 既有正規化來源（原生 SPD／SMBIOS）。不讀任何硬體。</summary>
    public static MemWmiCrossState Cross(IReadOnlyList<WmiPhysicalMemoryRow> wmi,
        IReadOnlyList<SpdAuditSourceInput>? native, IReadOnlyList<SpdAuditSourceInput>? smbios)
    {
        ArgumentNullException.ThrowIfNull(wmi);
        int nativeCount = native?.Count(x => x.Kind == SpdAuditSourceKind.NativeSpd) ?? 0;
        int smbiosCount = smbios?.Count(x => x.Kind == SpdAuditSourceKind.Smbios) ?? 0;
        var candidates = (native ?? []).Concat(smbios ?? []).Where(x => x.Kind is SpdAuditSourceKind.NativeSpd or SpdAuditSourceKind.Smbios).ToList();
        var unused = new HashSet<int>(Enumerable.Range(0, candidates.Count));

        int compared = 0, serialBad = 0, capacityBad = 0, partBad = 0;
        foreach (var w in wmi)
        {
            // 序號唯一精確配對（沿用稽核服務「序號最強」的慣例）；其餘不猜。
            int? match = null;
            if (Normalize(w.SerialNumber) is { } wSerial)
            {
                var serialHits = unused.Where(j => Normalize(candidates[j].SerialNumber) == wSerial).ToList();
                if (serialHits.Count == 1) match = serialHits[0];
            }
            // 沒有序號可配時，退而以料號唯一精確配對；仍配不上就列「未配對」，不強配。
            if (match is null && Normalize(w.PartNumber) is { } wPart)
            {
                var partHits = unused.Where(j => Normalize(candidates[j].PartNumber) == wPart).ToList();
                if (partHits.Count == 1) match = partHits[0];
            }
            if (match is null) continue;
            unused.Remove(match.Value);
            compared++;
            var c = candidates[match.Value];

            long? wMiB = w.CapacityBytes is > 0 ? w.CapacityBytes / (1024 * 1024) : null;
            if (wMiB is > 0 && c.CapacityMiB is > 0 && wMiB != c.CapacityMiB) capacityBad++;
            if (Normalize(w.SerialNumber) is { } ws && Normalize(c.SerialNumber) is { } cs && ws != cs) serialBad++;
            if (Normalize(w.PartNumber) is { } wp && Normalize(c.PartNumber) is { } cp && wp != cp) partBad++;
        }
        return new(wmi.Count, nativeCount, smbiosCount, compared, serialBad, capacityBad, partBad);
    }

    /// <summary>WMI 通路（極薄）：實體記憶體的對照欄位；查詢失敗回 null 標三態。</summary>
    internal static IReadOnlyList<WmiPhysicalMemoryRow>? ReadWmi()
    {
        try
        {
            var list = new List<WmiPhysicalMemoryRow>();
            using var s = new System.Management.ManagementObjectSearcher(
                "SELECT DeviceLocator, BankLabel, Manufacturer, PartNumber, SerialNumber, Capacity, Speed FROM Win32_PhysicalMemory");
            foreach (System.Management.ManagementObject o in s.Get()) using (o)
            {
                list.Add(new WmiPhysicalMemoryRow(
                    o["DeviceLocator"]?.ToString(),
                    o["Manufacturer"]?.ToString(),
                    o["PartNumber"]?.ToString(),
                    o["SerialNumber"]?.ToString(),
                    o["Capacity"] is null ? null : Convert.ToInt64(o["Capacity"]),
                    o["Speed"] is null ? null : Convert.ToInt32(o["Speed"])));
            }
            return list;
        }
        catch { return null; }
    }

    /// <summary>與 <see cref="SpdConsistencyAuditService"/> 同款的正規化慣例：去空白、通用值視為缺、只留英數大寫。</summary>
    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string trimmed = value.Trim();
        if (trimmed.Length == 0) return null;
        string upper = string.Concat(trimmed.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        // 空殼序號／料號（全 0、全 F）與通用填充值一樣視為缺，不參與配對與矛盾判定。
        if (upper.Length == 0 || upper.All(c => c == '0') || upper.All(c => c == 'F')) return null;
        return upper;
    }
}
