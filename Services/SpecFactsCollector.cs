using System.Windows;

namespace XinSpect;

/// <summary>
/// 從執行中的檢視模型湊出 <see cref="SpecFacts"/>。
/// </summary>
/// <remarks>
/// 與 <c>UpgradeFactsCollector</c>／<c>BottleneckFactsCollector</c> 同一個分工：
/// 取值放這裡、產生文字放純函式那邊，這樣文字的規則才測得動。
/// <para>
/// 取不到就留空字串，交給 <see cref="SpecSummary"/> 統一寫成「—」——這裡不負責決定
/// 讀不到要怎麼呈現，那是文字層的事。
/// </para>
/// </remarks>
public static class SpecFactsCollector
{
    public static SpecFacts Collect(MainViewModel vm) => new()
    {
        Os = Clean(vm.System.OsName),
        OsVersion = Clean(vm.System.OsVersion),

        Cpu = Clean(vm.Cpu.Name),
        Cores = vm.Cpu.Cores,
        Threads = vm.Cpu.Threads,
        CpuMaxMHz = vm.Cpu.MaxClockMHz,

        Board = Join(Clean(vm.System.BoardVendor), Clean(vm.System.BoardModel)),
        Bios = Clean(vm.System.BiosVersion),

        RamGB = vm.Live?.MemTotalGB ?? 0,
        RamDetail = RamDetail(vm),

        Gpu = Gpu(vm),
        SystemDisk = SystemDisk(vm),
        Display = Display(),
    };

    /// <summary>記憶體補述：型別與速率＋條數。條數對回答「還能不能插」很有用。</summary>
    private static string RamDetail(MainViewModel vm)
    {
        var mods = vm.Modules.Where(m => m.CapacityGB > 0).ToList();
        if (mods.Count == 0) return "";

        string type = Clean(mods[0].MemoryType);
        int speed = mods.Max(m => m.ConfiguredSpeedMHz);
        string head = type.Length > 0 && speed > 0 ? $"{type}-{speed}"
                    : type.Length > 0 ? type
                    : speed > 0 ? $"{speed} MT/s"
                    : "";
        string count = $"{mods.Count} 條";
        return head.Length > 0 ? $"{head} ・ {count}" : count;
    }

    /// <summary>顯示卡：優先用深度規格裡的名稱，其次用 NVML 回報的名稱。</summary>
    private static string Gpu(MainViewModel vm)
    {
        if (vm.GpuDetails.Count > 0 && Clean(vm.GpuDetails[0].Name) is { Length: > 0 } n) return n;
        return Clean(vm.GpuOc.GpuName);
    }

    /// <summary>系統碟：取容量最大的那顆的型號。序號一律不取——貼到論壇用不到。</summary>
    private static string SystemDisk(MainViewModel vm)
    {
        var disk = vm.PhysicalDisks.OrderByDescending(d => d.SizeBytes).FirstOrDefault();
        if (disk is null) return "";
        string model = Clean(disk.Model);
        double gb = disk.SizeBytes / 1e9;
        return gb > 0 && model.Length > 0 ? $"{model}（{gb:0} GB）" : model;
    }

    /// <summary>主顯示器的解析度。取自 WPF 的系統參數，不需要任何硬體查詢。</summary>
    private static string Display()
    {
        try
        {
            double w = SystemParameters.PrimaryScreenWidth, h = SystemParameters.PrimaryScreenHeight;
            return w > 0 && h > 0 ? $"{w:0} × {h:0}" : "";
        }
        catch { return ""; }
    }

    private static string Clean(string? s)
        => string.IsNullOrWhiteSpace(s) || s.Trim() == "—" ? "" : s.Trim();

    private static string Join(string a, string b)
        => a.Length > 0 && b.Length > 0 ? $"{a} {b}" : a.Length > 0 ? a : b;
}
