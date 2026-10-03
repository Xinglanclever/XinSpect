namespace XinSpect;

/// <summary>
/// 帶外管理與 RAID OOB 的三態事實（V7 WP18／WP24／A18／A23）。
/// 三態分離（A18 硬性要求 4）：<b>NotApplicable＝無 BMC／無 RAID 控制器</b>（環境事實）；
/// <b>NotSupported＝有硬體但通路未實作</b>（KCS/SMBus 無法在本機驗證，不出貨）。
/// 本工具只宣稱「支援 IPMI／MegaRAID <b>訊息解碼</b>」（純解碼器，見 IpmiDecoder／MegaRaidDecoder），
/// 不宣稱支援 IPMI/MegaRAID 本身。
/// </summary>
public static class OobFactsService
{
    private const string Category = "帶外管理";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<bool?>? bmcPresentProbe = null, Func<bool?>? raidPresentProbe = null)
    {
        bool? bmc = (bmcPresentProbe ?? ProbeBmcPresent)();
        bool? raid = (raidPresentProbe ?? ProbeRaidPresent)();
        return [BmcFact(bmc, at), RaidFact(raid, at)];
    }

    /// <summary>BMC 存在性事實：true（有 BMC）→ NotSupported（KCS 未實作）；false（無 BMC）→ NotApplicable；未知 → 如實標。</summary>
    public static HardwareFact BmcFact(bool? bmcPresent, DateTimeOffset at)
    {
        const string key = "oob.ipmi", name = "帶外管理（IPMI）", source = "SMBIOS Type 38（IPMI 裝置）存在性";
        return bmcPresent switch
        {
            true => new HardwareFact(key, Category, name,
                "本機有 BMC（SMBIOS Type 38）——但 KCS 埠存取（0xCA2/0xCA3）未實作且無法在本機驗證，不出貨；本工具僅支援 IPMI 訊息解碼（SEL/FRU/SDR，純解碼器未施測）",
                "", source, FactTrustLevel.Derived, false, at, null, FactAvailability.NotSupported,
                "通路未實作：無法驗證的通路不出貨（誠實契約）"),
            false => new HardwareFact(key, Category, name,
                "本機無 BMC（SMBIOS 無 Type 38 IPMI 裝置）——帶外管理不適用；本工具僅支援 IPMI 訊息解碼（SEL/FRU/SDR，純解碼器未施測）",
                "", source, FactTrustLevel.Derived, false, at, null, FactAvailability.NotApplicable,
                "本機無 BMC：無 IPMI 通路可施測"),
            _ => new HardwareFact(key, Category, name, "", "", source, FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotSupported, "BMC 存在性無法確認（SMBIOS 列舉不可用）——不推測"),
        };
    }

    /// <summary>RAID OOB 事實：探測 PCI 類別碼 0104（RAID）/0107（SAS）——本機無控制器 → NotApplicable。</summary>
    public static HardwareFact RaidFact(bool? raidPresent, DateTimeOffset at)
    {
        const string key = "raid.oob", name = "RAID 帶外（MegaRAID）", source = "PCI 類別碼 0104/0107 盤點";
        return raidPresent switch
        {
            true => new HardwareFact(key, Category, name,
                "本機有 RAID 控制器——但 MegaRAID SMBus 帶外通路未實作且無法驗證，不出貨；本工具僅支援 MFI 訊框解碼（純解碼器未施測）",
                "", source, FactTrustLevel.Derived, false, at, null, FactAvailability.NotSupported,
                "通路未實作：無法驗證的通路不出貨（誠實契約）"),
            false => new HardwareFact(key, Category, name,
                "本機無 RAID 控制器（PCI 類別碼 0104/0107 未現身）——RAID 帶外管理不適用；本工具僅支援 MFI 訊框解碼（純解碼器未施測）",
                "", source, FactTrustLevel.Derived, false, at, null, FactAvailability.NotApplicable,
                "本機無 RAID 控制器：無 MegaRAID 通路可施測"),
            _ => new HardwareFact(key, Category, name, "", "", source, FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotSupported, "RAID 控制器存在性無法確認（PCI 盤點不可用）——不推測"),
        };
    }

    // ── 生產探測（唯讀；測試以注入委派取代）──

    private static bool? ProbeBmcPresent()
    {
        try
        {
            var smbios = new SmbiosService();
            if (!smbios.Available) return null;
            return smbios.Structs.Any(s => s.Type == 38); // SMBIOS Type 38＝IPMI 裝置
        }
        catch { return null; }
    }

    private static bool? ProbeRaidPresent()
    {
        try
        {
            using var pci = new WinRing0PciConfigReader();
            if (!pci.Available) return null;
            for (byte dev = 0; dev < 32; dev++)
            {
                for (byte fn = 0; fn < 8; fn++)
                {
                    var id = pci.ReadDword(0, dev, fn, 0x00);
                    if (id is null or 0xFFFF_FFFF or 0) continue;
                    var cls = pci.ReadDword(0, dev, fn, 0x08);
                    if (cls is { } c && (byte)(c >> 24) == 0x01 && ((c >> 16) & 0xFF) is 0x04 or 0x07)
                        return true; // RAID 或 SAS 控制器
                }
            }
            return false;
        }
        catch { return null; }
    }
}
