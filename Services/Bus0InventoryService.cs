namespace XinSpect;

/// <summary>
/// Bus 0 裝置盤點（V7 WP30／A13 的知識層面）：以 PCI 設定空間走訪 bus 0 的 32 個裝置槽，
/// 逐功能用 PCI-SIG 類別碼／廠商 ID 知識（<see cref="PciKnowledge"/>）給出「規格有據的名字」。
/// 誠實界線：
/// ① 多功能位元（header type bit7）未設的裝置不掃 fn 1–7——那是規格行為，不是偷工；
/// ② device 型號不對照（每代更新、文件分散），只報原始 ID 供稽核；
/// ③ 讀取錯誤逐次計數進摘要，全部失敗時整組三態。
/// </summary>
public static class Bus0InventoryService
{
    private const string Category = "PCI 裝置";

    /// <summary>SPI 控制器（0:1F.5）的盤點事實鍵——供對帳規則交叉引用。</summary>
    public const string SpiControllerKey = "pci.dev.1f.5";

    public static IReadOnlyList<HardwareFact> Collect(IPciConfigReader pci, DateTimeOffset at)
    {
        const string invKey = "pci.bus0.inventory", invName = "Bus 0 裝置盤點", source = "PCI 設定空間 0x00/0x08/0x0C";
        if (!pci.Available)
            return [Unavailable(invKey, invName, source, at, FactAvailability.InsufficientPrivilege,
                pci.UnavailableReason ?? "缺 ring0：特權讀取未就緒")];

        var facts = new List<HardwareFact>();
        int devices = 0, functions = 0, readErrors = 0;
        for (byte dev = 0; dev < 32; dev++)
        {
            var id0 = pci.ReadDword(0, dev, 0, 0x00);
            if (id0 is null) { readErrors++; continue; }
            if (id0.Value == 0xFFFFFFFF) continue; // 空槽：PCI 慣例全 F，不是錯誤

            devices++;
            functions++;
            facts.Add(DeviceFact(dev, 0, id0.Value, pci, at));

            // 多功能位元：header type（0x0C dword bits[23:16]）bit7。未設就不掃 fn 1–7（規格行為）。
            bool multiFunction = pci.ReadDword(0, dev, 0, 0x0C) is { } header && (header & (1u << 23)) != 0;
            if (!multiFunction) continue;

            for (byte fn = 1; fn < 8; fn++)
            {
                var id = pci.ReadDword(0, dev, fn, 0x00);
                if (id is null) { readErrors++; continue; }
                if (id.Value == 0xFFFFFFFF) continue;
                functions++;
                facts.Add(DeviceFact(dev, fn, id.Value, pci, at));
            }
        }

        if (facts.Count == 0 && readErrors > 0)
            return [Unavailable(invKey, invName, source, at, FactAvailability.ReadError,
                $"bus 0 探頭讀取失敗 {readErrors} 次——PCI 設定空間不可達")];

        var notes = new List<string>();
        if (readErrors > 0) notes.Add($"{readErrors} 次讀取失敗（該槽位如實缺席，不猜）");
        facts.Add(new HardwareFact(invKey, Category, invName,
            $"bus 0：{devices} 個裝置、{functions} 個功能{(notes.Count > 0 ? "；" + string.Join("；", notes) : "")}",
            "", source, FactTrustLevel.Measured, false, at,
            NumericValue: devices));
        return facts;
    }

    private static HardwareFact DeviceFact(byte dev, byte fn, uint idRaw, IPciConfigReader pci, DateTimeOffset at)
    {
        string key = $"pci.dev.{dev:x2}.{fn}";
        string name = $"PCI 0:{dev:X2}.{fn}";
        var ids = PciKnowledge.DecodeVendorDevice(idRaw);
        var clsRaw = pci.ReadDword(0, dev, fn, 0x08);
        string value = clsRaw is { } c
            ? PciKnowledge.Describe(ids, PciKnowledge.DecodeClassCode(c))
            : $"Vendor 0x{ids.VendorId:X4}:0x{ids.DeviceId:X4}（類別碼讀取失敗，只報原始 ID）";
        return new HardwareFact(key, Category, name, value, "", "PCI 設定空間 0x00/0x08",
            FactTrustLevel.Measured, false, at);
    }

    private static HardwareFact Unavailable(string key, string name, string source, DateTimeOffset at,
        FactAvailability availability, string reason) =>
        new(key, Category, name, "", "", source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
