namespace XinSpect;

/// <summary>
/// CMOS/RTC 唯讀事實（V7 WP6／A10）：RTC 有效位（0x0D VRT＝電池掉電判斷）、RTC 時鐘
/// （0x00/02/04，經 0x0A/0x0B 格式位解碼）、PC-AT 校驗和（0x2E/0x2F 覆蓋 0x10–0x2D）。
/// <b>嚴禁寫入</b>（V7 §10：寫 CMOS＝磚化風險）——本服務連寫 0x71 的程式碼路徑都不存在，
/// 0x70 選址一律 bit7=0（保持 NMI 啟用；若系統原以 NMI 停用執行——極罕見——第一次選址會
/// 使 NMI 重新啟用，這是 CMOS 讀取協定的固有限制，如實記錄於此）。
/// 廠商 BIOS 設定區（0x10–0x2D）可讀但<b>刻意不解碼</b>：佈局依機種而異，未對準規格前
/// 任何解讀都違反誠實原則（V7 §14「CMOS／BIOS layout 廠商相依 → 標未知，不猜」）。
/// </summary>
public static class CmosService
{
    private const string Category = "CMOS";
    private const uint IndexPort = 0x70, DataPort = 0x71;
    private const byte LastRegister = 0x3F;

    public static IReadOnlyList<HardwareFact> Collect(IIoPortAccess io, DateTimeOffset at)
    {
        if (!io.Available)
        {
            var all = CmosFactKeys();
            return all.Zip(CmosFactNames()).Select(p => Unavailable(p.First, p.Second, "CMOS 0x70/0x71 唯讀", at,
                FactAvailability.InsufficientPrivilege, io.UnavailableReason ?? "缺 I/O 埠存取")).ToList();
        }
        var regs = Snapshot(io);
        if (regs is null)
            return CmosFactKeys().Zip(CmosFactNames()).Select(p => Unavailable(p.First, p.Second, "CMOS 0x70/0x71 唯讀", at,
                FactAvailability.ReadError, "CMOS 埠讀取失敗（選址或資料埠無回應）")).ToList();

        return
        [
            VrtFact(regs, at),
            TimeFact(regs, at),
            ChecksumFact(regs, at),
        ];
    }

    /// <summary>讀 0x00–0x3F 快照：寫 0x70 選址（bit7=0 保持 NMI）→ 讀 0x71。任一埠讀失敗回 null。</summary>
    public static byte[]? Snapshot(IIoPortAccess io)
    {
        var regs = new byte[LastRegister + 1];
        for (int i = 0; i <= LastRegister; i++)
        {
            if (!io.OutByte(IndexPort, (byte)(i & 0x7F))) return null;
            if (io.InByte(DataPort) is not { } value) return null;
            regs[i] = value;
        }
        return regs;
    }

    private static HardwareFact VrtFact(byte[] regs, DateTimeOffset at)
    {
        const string key = "cmos.rtc_valid", name = "RTC 電池與時間有效位", source = "CMOS 0x0D bit7（VRT）";
        byte vrt = regs[0x0D];
        return vrt == 0xFF
            ? Unavailable(key, name, source, at, FactAvailability.NotSupported, "暫存器回全 F——此平台未實作 VRT 位，無法判斷")
            : new HardwareFact(key, Category, name,
                Cmos.VrtValid(vrt) ? "有效（VRT=1）：電池供電正常" : "RTC 掉電（VRT=0）：電池失效或曾斷電，時間與設定可能已重置",
                "", source, FactTrustLevel.Measured, false, at, Cmos.VrtValid(vrt) ? 1 : 0);
    }

    private static HardwareFact TimeFact(byte[] regs, DateTimeOffset at)
    {
        const string key = "cmos.rtc_time", name = "RTC 時鐘", source = "CMOS 0x00/02/04（經 0x0A/0x0B 格式位）";
        var (uip, binary, hour24) = Cmos.DecodeStatus(regs[0x0A], regs[0x0B]);
        if (uip)
            return Unavailable(key, name, source, at, FactAvailability.ReadError,
                "更新進行中（UIP=1）——此刻讀值不完整，不以半新半舊的時間冒充");
        var t = Cmos.DecodeTime(regs[0x00], regs[0x02], regs[0x04], binary, hour24);
        return t is null
            ? Unavailable(key, name, source, at, FactAvailability.ReadError,
                $"格式異常（raw {regs[0x04]:X2}:{regs[0x02]:X2}:{regs[0x00]:X2}，{(binary ? "二進位" : "BCD")}/{(hour24 ? "24h" : "12h")}）——不解碼垃圾")
            : new HardwareFact(key, Category, name,
                $"{t.Value.Hour:00}:{t.Value.Minute:00}:{t.Value.Second:00}（RTC 本地時間，{(binary ? "二進位" : "BCD")}/{(hour24 ? "24h" : "12h")}格式）",
                "", source, FactTrustLevel.Measured, false, at);
    }

    private static HardwareFact ChecksumFact(byte[] regs, DateTimeOffset at)
    {
        const string key = "cmos.checksum", name = "PC-AT 設定區校驗和", source = "CMOS 0x2E/0x2F 覆蓋 0x10–0x2D";
        bool matches = Cmos.ChecksumMatches(regs);
        return new HardwareFact(key, Category, name,
            matches
                ? "校驗和相符——設定區自上次儲存後未被更動（或 BIOS 恰好維護一致）"
                : "校驗和不符——設定區曾被更動、未初始化，或此 BIOS 不維護 PC-AT 校驗和（依機種而異，非必然異常）",
            "", source, FactTrustLevel.Measured, false, at, matches ? 1 : 0);
    }

    private static string[] CmosFactKeys() => ["cmos.rtc_valid", "cmos.rtc_time", "cmos.checksum"];
    private static string[] CmosFactNames() => ["RTC 電池與時間有效位", "RTC 時鐘", "PC-AT 設定區校驗和"];

    private static HardwareFact Unavailable(string key, string name, string source, DateTimeOffset at,
        FactAvailability availability, string reason) =>
        new(key, Category, name, "", "", source, FactTrustLevel.Unknown, false, at,
            null, availability, reason);
}
