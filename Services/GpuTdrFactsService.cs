using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// GPU 穩定性缺口補齊：① TDR 逾時設定（登錄檔 GraphicsDrivers——TdrLevel／TdrDelay／
/// TdrDpcDelay；未設定＝Windows 預並明說，不猜成已設定值）② NVML 退休頁（retired pages，
/// NAND 瑕疵退休計數，僅 NVIDIA——無卡 NotApplicable）。
/// </summary>
public static class GpuTdrFactsService
{
    private const string Category = "顯示卡";
    private const string Source = "登錄檔 HKLM\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<Dictionary<string, int?>>? registryProbe = null)
    {
        var values = registryProbe ?? ReadRegistry;
        int? level = values().TryGetValue("TdrLevel", out var l) ? l : null;
        int? delay = values().TryGetValue("TdrDelay", out var d) ? d : null;
        int? dpc = values().TryGetValue("TdrDpcDelay", out var p) ? p : null;

        var facts = new List<HardwareFact>
        {
            new("gpu.tdr.level", Category, "TDR 等級（TdrLevel）",
                level is { } lv
                    ? lv switch
                    {
                        0 => "0：TDR 偵測停用（危險——GPU 掛死會凍結全系統）",
                        1 => "1：錯誤檢查（Bug Check）",
                        2 => "2：僅恢復（無 Bug Check）",
                        3 => "3：恢復＋逾時中斷（Windows 預設行為）",
                        var other => $"TdrLevel {other}（未收錄）",
                    }
                    : "未設定（使用 Windows 預設＝3：恢復＋逾時中斷）",
                "", Source, FactTrustLevel.Reported, false, at, level),
            new HardwareFact("gpu.tdr.delay", Category, "TDR 逾時秒數（TdrDelay）",
                delay is { } dd ? $"{dd} 秒（登錄檔自訂）" : "未設定（使用 Windows 預設＝2 秒）", "秒",
                Source, FactTrustLevel.Reported, false, at, delay),
            new HardwareFact("gpu.tdr.dpc", Category, "TDR DPC 逾時（TdrDpcDelay）",
                dpc is { } dp ? $"{dp} 秒（登錄檔自訂）" : "未設定（使用 Windows 預設）", "秒",
                Source, FactTrustLevel.Reported, false, at, dpc),
        };

        // NVML 退休頁（僅 NVIDIA；無卡＝NotApplicable 不是錯誤）
        facts.Add(NvmlRetiredPagesFact(at));
        return facts;
    }

    private static HardwareFact NvmlRetiredPagesFact(DateTimeOffset at)
    {
        try
        {
            if (NvmlInterop.Init() != 0)
                return new HardwareFact("gpu.retired_pages", Category, "顯存退休頁（retired pages）", "", "",
                    "NVML nvmlDeviceGetRetiredPages", FactTrustLevel.Unknown, false, at, null,
                    FactAvailability.NotApplicable, "沒有可用的 NVIDIA GPU／NVML——無此硬體不是錯誤");
            try
            {
                if (NvmlInterop.GetCount(out uint count) != 0 || count == 0)
                    return new HardwareFact("gpu.retired_pages", Category, "顯存退休頁（retired pages）", "", "",
                        "NVML nvmlDeviceGetRetiredPages", FactTrustLevel.Unknown, false, at, null,
                        FactAvailability.NotApplicable, "沒有可用的 NVIDIA GPU");
                if (NvmlInterop.GetHandleByIndex(0, out IntPtr dev) != 0)
                    return new HardwareFact("gpu.retired_pages", Category, "顯存退休頁（retired pages）", "", "",
                        "NVML nvmlDeviceGetRetiredPages", FactTrustLevel.Unknown, false, at, null,
                        FactAvailability.ReadError, "NVML 取得裝置失敗");
                int rc1 = NvmlInterop.GetRetiredPages(dev, 0, out uint single);   // source 0＝single-bit
                int rc2 = NvmlInterop.GetRetiredPages(dev, 1, out uint dbl);      // source 1＝double-bit
                if (rc1 != 0 && rc2 != 0)
                    return new HardwareFact("gpu.retired_pages", Category, "顯存退休頁（retired pages）", "", "",
                        "NVML nvmlDeviceGetRetiredPages", FactTrustLevel.Unknown, false, at, null,
                        FactAvailability.ReadError, "NVML 查詢失敗（此卡可能不支援退休頁報告）");
                uint total = (rc1 == 0 ? single : 0) + (rc2 == 0 ? dbl : 0);
                return new HardwareFact("gpu.retired_pages", Category, "顯存退休頁（retired pages）",
                    $"{total} 頁（single-bit {single}、double-bit {dbl}）——NAND 瑕疵退休計數", "頁",
                    "NVML nvmlDeviceGetRetiredPages_v2", FactTrustLevel.Reported, false, at, total);
            }
            finally { NvmlInterop.Shutdown(); }
        }
        catch { return new HardwareFact("gpu.retired_pages", Category, "顯存退休頁（retired pages）", "", "",
            "NVML nvmlDeviceGetRetiredPages", FactTrustLevel.Unknown, false, at, null,
            FactAvailability.ReadError, "NVML 例外"); }
    }

    private static Dictionary<string, int?> ReadRegistry()
    {
        var map = new Dictionary<string, int?>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            if (key is null) return map;
            foreach (var name in new[] { "TdrLevel", "TdrDelay", "TdrDpcDelay" })
                map[name] = key.GetValue(name) is int v ? v : null;
        }
        catch { /* 讀不到就當未設定 */ }
        return map;
    }
}
