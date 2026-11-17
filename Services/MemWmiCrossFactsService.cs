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
/// 對照面：條數、容量、料號。WMI 與原生 SPD／SMBIOS 分別以兩側唯一有效序號配對；
/// 料號與清單順序不是實體身份，配不上就標「未配對」；缺任何一源如實標，不猜。
/// 只列矛盾<b>計數</b>，不列序號或料號原值（遮蔽慣例）。
/// </para>
/// </summary>
public static class MemWmiCrossFactsService
{
    public const string MemWmiCrossKey = "mem.wmi.cross";

    private const string Category = "記憶體";

    /// <summary>單一來源的對照數；缺欄不算矛盾，不能視為已核一致。</summary>
    public sealed record MemWmiSourceState(int ComparedModules, int CapacityConflicts, int PartConflicts);

    /// <summary>
    /// ComparedModules 與衝突各欄按已配對 WMI 列去重，不把同一模組的兩來源算成兩條。
    /// SerialConflicts 保留介面相容；序號是配對身份，沒有獨立身份就不判序號矛盾。
    /// NativeComparison／SmbiosComparison 保留每個來源的觀測統計，不宣稱來源獨立。
    /// </summary>
    public sealed record MemWmiCrossState(
        int WmiModules, int NativeModules, int SmbiosModules,
        int ComparedModules, int SerialConflicts, int CapacityConflicts, int PartConflicts)
    {
        public MemWmiSourceState NativeComparison { get; init; } = new(0, 0, 0);
        public MemWmiSourceState SmbiosComparison { get; init; } = new(0, 0, 0);
    }

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
        string missingNote = missing.Length == 0 ? "本次原生 SPD／SMBIOS 輸入齊備" : $"缺源：{missing}（如實標，不補值）";
        string detail =
            $"WMI {state.WmiModules} 條、原生 SPD {state.NativeModules} 條、SMBIOS {state.SmbiosModules} 條；" +
            $"可配對 {state.ComparedModules} 條（按 WMI 列去重），容量矛盾 {state.CapacityConflicts}、料號矛盾 {state.PartConflicts}。" +
            $"WMI×原生 SPD：配對 {state.NativeComparison.ComparedModules}、容量矛盾 {state.NativeComparison.CapacityConflicts}、料號矛盾 {state.NativeComparison.PartConflicts}；" +
            $"WMI×SMBIOS：配對 {state.SmbiosComparison.ComparedModules}、容量矛盾 {state.SmbiosComparison.CapacityConflicts}、料號矛盾 {state.SmbiosComparison.PartConflicts}。" +
            $"{missingNote}。序號僅作配對鍵，不驗證序號真實性；重複或缺序號不以料號／順序猜配。" +
            "缺欄不計矛盾，不表示已核一致；各通路可能共用 SPD／韌體資料，不構成獨立驗證來源；矛盾是差異，不等同於偽造。";
        return new HardwareFact(MemWmiCrossKey, Category, "WMI 記憶體對照", detail, "條",
            "WMI Win32_PhysicalMemory×既有 SPD 解碼（唯讀）", FactTrustLevel.Reported, false, at,
            state.WmiModules);
    }

    /// <summary>純對照：WMI 列 × 既有正規化來源（原生 SPD／SMBIOS）。不讀任何硬體。</summary>
    public static MemWmiCrossState Cross(IReadOnlyList<WmiPhysicalMemoryRow> wmi,
        IReadOnlyList<SpdAuditSourceInput>? native, IReadOnlyList<SpdAuditSourceInput>? smbios)
    {
        ArgumentNullException.ThrowIfNull(wmi);
        var nativeRows = (native ?? []).Where(x => x.Kind == SpdAuditSourceKind.NativeSpd).ToArray();
        var smbiosRows = (smbios ?? []).Where(x => x.Kind == SpdAuditSourceKind.Smbios).ToArray();
        var n = CompareSource(wmi, nativeRows);
        var s = CompareSource(wmi, smbiosRows);
        return new(wmi.Count, nativeRows.Length, smbiosRows.Length,
            n.Compared.Union(s.Compared).Count(), 0,
            n.CapacityBad.Union(s.CapacityBad).Count(), n.PartBad.Union(s.PartBad).Count())
        {
            NativeComparison = new(n.Compared.Count, n.CapacityBad.Count, n.PartBad.Count),
            SmbiosComparison = new(s.Compared.Count, s.CapacityBad.Count, s.PartBad.Count),
        };
    }

    private sealed record SourceComparison(HashSet<int> Compared, HashSet<int> CapacityBad, HashSet<int> PartBad);

    private static SourceComparison CompareSource(IReadOnlyList<WmiPhysicalMemoryRow> wmi,
        IReadOnlyList<SpdAuditSourceInput> candidates)
    {
        // Build uniqueness from the complete source inventories, never a shrinking unused set.
        // Duplicate serials on either side remain ambiguous regardless of row order or part.
        var uniqueWmi = wmi.Select((row, index) => (Row: row, Index: index, Serial: NormalizeSerial(row.SerialNumber)))
            .Where(x => x.Serial is not null).GroupBy(x => x.Serial!, StringComparer.Ordinal)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var uniqueCandidates = candidates.Select(row => (Row: row, Serial: NormalizeSerial(row.SerialNumber)))
            .Where(x => x.Serial is not null).GroupBy(x => x.Serial!, StringComparer.Ordinal)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Row, StringComparer.Ordinal);
        var compared = new HashSet<int>();
        var capacityBad = new HashSet<int>();
        var partBad = new HashSet<int>();
        foreach (var (serial, observation) in uniqueWmi)
        {
            if (!uniqueCandidates.TryGetValue(serial, out var candidate)) continue;
            compared.Add(observation.Index);
            var row = observation.Row;
            long? wMiB = row.CapacityBytes is > 0 ? row.CapacityBytes / (1024 * 1024) : null;
            if (wMiB is > 0 && candidate.CapacityMiB is > 0 && wMiB != candidate.CapacityMiB)
                capacityBad.Add(observation.Index);
            if (NormalizePart(row.PartNumber) is { } wp && NormalizePart(candidate.PartNumber) is { } cp && wp != cp)
                partBad.Add(observation.Index);
        }
        return new(compared, capacityBad, partBad);
    }

    private static string? NormalizeSerial(string? value)
    {
        var serial = Normalize(value);
        return serial is "UNKNOWN" or "NOTSPECIFIED" or "NOTAVAILABLE" or "NOTAPPLICABLE" or "NA"
            or "NONE" or "NULL" or "DEFAULTSTRING" or "SYSTEMSERIALNUMBER" or "SYSTEMPRODUCTNAME"
            or "SYSTEMVERSION" or "SYSTEMMANUFACTURER" or "TOBEFILLEDBYOEM"
            or "MANUFACTURER" or "PARTNUMBER" or "SERIALNUMBER" ? null : serial;
    }

    private static string? NormalizePart(string? value)
    {
        if (value is null) return null;
        string raw = value.Trim();
        // Reject explicit raw placeholders before punctuation removal. N/A means missing;
        // literal NA, PARTNUMBER or SERIALNUMBER may be real parts and must not inherit serial's blacklist.
        if (SmbiosService.IsPlaceholder(raw)
            || raw.Equals("N/A", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("Not Available", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("Not Applicable", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("Null", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("To be filled by OEM", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("Part Number", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("Serial Number", StringComparison.OrdinalIgnoreCase)) return null;
        return Normalize(raw);
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
