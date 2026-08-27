using LibreHardwareMonitor.Hardware;

namespace XinSpect;

/// <summary>
/// SensorService 的「系統風扇控制」擴充：沿用同一顆 LHM Computer（已啟用 IsControllerEnabled），
/// 列舉主機板／Super I/O／嵌入式控制器上可由軟體寫入的風扇，並與對應轉速（RPM）配對。
/// 顯示卡風扇不在此列——那由「顯示卡超頻」分頁以 NVML 控制，兩套介面分工避免互搶。
/// 一律真實寫入硬體；程式關閉時（Dispose）自動把全部風扇還原為 BIOS／自動控制。
/// </summary>
public sealed partial class SensorService
{
    private readonly List<FanControlRow> _fanControls = new();

    /// <summary>本機可由軟體控制的系統風扇（可能為空——多數筆電／品牌機由 EC 全權管理）。</summary>
    public IReadOnlyList<FanControlRow> FanControls => _fanControls;

    /// <summary>是否至少有一顆可控風扇。</summary>
    public bool HasFanControls => _fanControls.Count > 0;

    /// <summary>目前是否停在「系統風扇控制」分頁；否則 Publish 略過每秒的風扇即時值更新。</summary>
    public bool FanControlsVisible { get; set; }

    // 初始化時由 BuildCache 呼叫一次，列舉全部可控風扇。
    private void CollectFanControls()
    {
        foreach (var hw in _computer.Hardware)
        {
            // 顯示卡風扇交由顯示卡超頻頁（NVML）控制，這裡略過以免兩套 UI 互相打架
            if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                continue;
            CollectFansIn(hw, FanGroupName(hw));
        }
    }

    // 遞迴收集某硬體（含子硬體，如主機板底下的 Super I/O 晶片）上的風扇控制。
    private void CollectFansIn(IHardware hw, string group)
    {
        var fans = hw.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList();
        foreach (var ctl in hw.Sensors.Where(s => s.SensorType == SensorType.Control && s.Control is not null))
        {
            int idx = CoreIndex(ctl.Name);
            // 以名稱中的編號（#1、#2…）把「控制%」對到「轉速 RPM」；配不到就取第一顆風扇
            ISensor? rpm = idx >= 0
                ? fans.FirstOrDefault(f => CoreIndex(f.Name) == idx) ?? fans.FirstOrDefault()
                : fans.FirstOrDefault();
            _fanControls.Add(new FanControlRow(group, ctl.Control!, ctl, rpm, idx));
        }
        foreach (var sub in hw.SubHardware)
            CollectFansIn(sub, $"{group} / {sub.Name}");
    }

    // 每秒由 Publish 呼叫（僅風扇分頁可見時）：更新每顆風扇的目前輸出%與轉速。
    private void TickFans()
    {
        foreach (var f in _fanControls) f.Tick();
    }

    /// <summary>把所有被手動接管的風扇還原為 BIOS／自動控制。程式關閉前務必呼叫。</summary>
    public void RestoreAllFansToAuto()
    {
        foreach (var f in _fanControls)
            try { f.RestoreAuto(); } catch { /* 個別風扇還原失敗不影響其餘 */ }
    }

    // 頂層硬體的顯示分組名（繁中）。子硬體名稱直接附在後面。
    private static string FanGroupName(IHardware hw) => hw.HardwareType switch
    {
        HardwareType.Motherboard => "主機板",
        HardwareType.Cpu => "處理器",
        _ => hw.Name,
    };
}
