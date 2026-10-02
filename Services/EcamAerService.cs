namespace XinSpect;

/// <summary>
/// 走 ACPI MCFG 取 ECAM 基底，逐裝置讀 4KB 擴充組態空間，以 PcieAer 純解碼器讀 AER 錯誤狀態。
/// 擴充組態空間（&gt;0xFF）只有 ECAM/MMIO 讀得到——後端不可用時整段三態標示，不假裝掃過；
/// 但 MCFG 本身 usermode 讀得到，ECAM 基底照常報 Present。
/// 掃描範圍：segment 0 從 StartBus 起最多 32 條 bus（探頭讀取量上限保護），超出範圍如實標「未掃」；
/// 逐裝置事實的 key 帶 bus 號，掃描中止時中止點如實寫進原因。
/// </summary>
public static class EcamAerService
{
    private const string Category = "PCIe";
    private const byte MaxBuses = 32;

    public static IReadOnlyList<HardwareFact> Collect(IMmioReader mmio, IAcpiTableSource acpi, DateTimeOffset at)
    {
        if (!acpi.Available)
            return [UnavailableFact("pcieaer.ecam", "ECAM 基底", "ACPI MCFG", at, FactAvailability.InsufficientPrivilege,
                acpi.UnavailableReason ?? "無法列舉 ACPI 表")];

        McfgEntry? primary = null;
        int otherSegments = 0;
        foreach (var t in acpi.ReadAll())
        {
            var entries = AcpiTable.McfgEntries(t);
            foreach (var e in entries)
            {
                if (primary is null && e.SegmentGroup == 0 && e.Base != 0) primary = e;
                else if (e.SegmentGroup != 0) otherSegments++;
            }
        }
        if (primary is null)
            return [UnavailableFact("pcieaer.ecam", "ECAM 基底", "ACPI MCFG", at, FactAvailability.NotApplicable,
                "平台未提供 MCFG 表（或 segment 0 條目基底為 0），無 ECAM 可循")];

        string segmentNote = otherSegments > 0
            ? $"；另有 {otherSegments} 個 segment 未納入掃描（驅動端批次列舉就緒後再開）"
            : "";
        var ecamFact = new HardwareFact("pcieaer.ecam", Category, "ECAM 基底",
            $"0x{primary.Base:X8}（MCFG segment 0，bus {primary.StartBus}-{primary.EndBus}）{segmentNote}", "", "ACPI MCFG",
            FactTrustLevel.Measured, false, at);

        if (!mmio.Available)
            return [ecamFact, UnavailableFact("pcieaer.scan", "PCIe AER 掃描", "ECAM 擴充組態空間", at,
                FactAvailability.InsufficientPrivilege,
                $"{mmio.UnavailableReason ?? "缺 MMIO 讀取"}；擴充組態空間（&gt;0xFF）須經 ECAM/MMIO")];

        byte lastBus = (byte)Math.Min(primary.EndBus, (int)primary.StartBus + MaxBuses - 1);
        string rangeNote = lastBus < primary.EndBus
            ? $"；bus {lastBus + 1}-{primary.EndBus} 未掃（探頭讀取量上限 {MaxBuses} 條 bus）"
            : "";
        var facts = new List<HardwareFact> { ecamFact };
        int devices = 0, withAer = 0;
        for (byte bus = primary.StartBus; bus <= lastBus; bus++)
        {
            for (byte dev = 0; dev < 32; dev++)
            {
                for (byte fn = 0; fn < 8; fn++)
                {
                    ulong addr = primary.Base + ((ulong)bus << 20 | (ulong)dev << 15 | (ulong)fn << 12);
                    var head = mmio.ReadBlock(addr, 0x10);
                    if (head is null)
                    {
                        facts.Add(UnavailableFact("pcieaer.scan", "PCIe AER 掃描", "ECAM 擴充組態空間", at,
                            FactAvailability.ReadError,
                            $"ECAM 讀取失敗（0x{addr:X}）——掃描中止於 bus {bus}{(mmio.LastFailReason is { } f1 ? $"：{f1}" : "")}"));
                        return facts;
                    }
                    if (BitConverter.ToUInt32(head, 0) == 0xFFFFFFFF) continue; // 不存在的裝置依 ECAM 慣例回全 F
                    devices++;

                    var page = mmio.ReadBlock(addr, 4096);
                    if (page is null)
                    {
                        facts.Add(UnavailableFact("pcieaer.scan", "PCIe AER 掃描", "ECAM 擴充組態空間", at,
                            FactAvailability.ReadError,
                            $"ECAM 讀取失敗（0x{addr:X}）——掃描中止於 bus {bus} dev {dev:X2}.{fn}{(mmio.LastFailReason is { } f2 ? $"：{f2}" : "")}"));
                        return facts;
                    }
                    if (PcieAer.FindAerCapOffset(page) is not { } aerOffset) continue;
                    withAer++;
                    var st = PcieAer.DecodeAer(page, aerOffset)!.Value;
                    facts.Add(new HardwareFact($"pcieaer.aer.{bus}.{dev}.{fn}", Category, $"AER {bus}:{dev:X2}.{fn}",
                        AerText(st, aerOffset), "", $"ECAM 0x{addr:X}（AER @0x{aerOffset:X}）",
                        FactTrustLevel.Measured, false, at));
                }
            }
        }

        facts.Add(new HardwareFact("pcieaer.scan", Category, "PCIe AER 掃描",
            $"掃描 bus {primary.StartBus}-{lastBus}：{devices} 個裝置、{withAer} 個帶 AER 能力{rangeNote}", "", "ECAM",
            FactTrustLevel.Measured, false, at));
        return facts;
    }

    private static HardwareFact UnavailableFact(string key, string name, string source, DateTimeOffset at,
        FactAvailability availability, string reason)
        => new(key, Category, name, "", "", source, FactTrustLevel.Unknown, false, at, null, availability, reason);

    // 錯誤位元名稱依 PCIe Base Spec 的 Status 暫存器定義（跨平台穩定）；保留位與未命名位元如實報位元號。
    private static readonly string?[] UncorrectableNames =
    [
        null, null, null, null, "資料鏈路協定錯誤", "突然斷線", null, null,
        null, null, null, null, "毒化 TLP", "流量控制協定錯誤", "完成逾時", "完成者中止",
        "非預期完成", "接收器溢位", "畸形 TLP", "ECRC 錯誤", "不支援的請求", "ACS 違規", "未修正內部錯誤", null,
        null, null, null, null, null, null, null, null,
    ];

    private static readonly string?[] CorrectableNames =
    [
        "接收器錯誤", null, null, null, null, null, "壞 TLP", "壞 DLLP",
        "重送計數迴轉", null, null, null, "重送計時器逾時", "勸告性非致命錯誤", "可修正內部錯誤", "標頭日誌溢位",
        null, null, null, null, null, null, null, null,
        null, null, null, null, null, null, null, null,
    ];

    private static string AerText(AerStatus st, int aerOffset)
    {
        var parts = new List<string>();
        if (st.UncorrectableStatus != 0) parts.Add($"未修正：{Bits(st.UncorrectableStatus, UncorrectableNames)}");
        if (st.CorrectableStatus != 0) parts.Add($"可修正：{Bits(st.CorrectableStatus, CorrectableNames)}");
        return parts.Count == 0 ? $"AER @0x{aerOffset:X}：無未修正、無可修正錯誤" : $"AER @0x{aerOffset:X}：" + string.Join("；", parts);
    }

    private static string Bits(uint raw, string?[] names)
    {
        var set = new List<string>();
        for (int i = 0; i < 32; i++)
            if ((raw & (1u << i)) != 0)
                set.Add(i < names.Length && names[i] is { } n ? n : $"位元{i}");
        return string.Join("、", set) + $"（0x{raw:X8}）";
    }
}
