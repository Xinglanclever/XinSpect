using System.Text;

namespace XinSpect;

/// <summary>
/// AI 評價的資料快照：把「這台機器現在是什麼、跑得怎麼樣」壓成一段精簡文字。
/// </summary>
/// <remarks>
/// 全部欄位皆取自真實讀值（WMI／LHM／CPU-Z 報告），沒有任何推測或佔位資料；
/// 讀不到者以其模型預設的「—」呈現，讓模型自己判斷該資訊不可用。
/// 刻意保持短小：這段文字每次對話都會前置送出，過長會擠壓模型的推理預算。
/// </remarks>
internal static class AiSnapshotBuilder
{
    public static string Build(MainViewModel vm)
    {
        var sb = new StringBuilder();
        var sys = vm.System;
        var cpu = vm.Cpu;
        var live = vm.Live;

        // ── 系統 ────────────────────────────────────────────────
        sb.AppendLine($"作業系統：{sys.OsName}（{sys.OsVersion}，{sys.OsArch}）");
        sb.AppendLine($"機型：{sys.SystemManufacturer} {sys.SystemModel}");
        sb.AppendLine($"主機板：{sys.BoardVendor} {sys.BoardModel}　BIOS：{sys.BiosVersion}（{sys.BiosDate}）");

        // ── 處理器 ──────────────────────────────────────────────
        sb.AppendLine($"處理器：{cpu.Name}　{cpu.Cores} 核 {cpu.Threads} 執行緒"
                    + $"　基準 {cpu.MaxClockMHz:0} MHz　製程 {vm.CpuDetail.Technology}　TDP {vm.CpuDetail.TdpLimit}");

        if (live is not null)
            sb.AppendLine($"處理器即時：頻率 {live.CpuClockText}　負載 {live.CpuLoadText}"
                        + $"　溫度 {live.CpuTempText}　功耗 {live.CpuPowerText}");

        // ── 記憶體 ──────────────────────────────────────────────
        if (live is not null)
            sb.AppendLine($"記憶體：{live.MemUsageText}　使用率 {live.MemLoadText}　模組 {vm.Modules.Count} 條");
        else
            sb.AppendLine($"記憶體：模組 {vm.Modules.Count} 條");

        if (vm.Timings.Status is { Length: > 0 } && vm.Timings.Status != "讀取中…")
            sb.AppendLine($"記憶體時序：{vm.Timings.Status}");

        // ── 顯示卡 ──────────────────────────────────────────────
        var gpu = live?.PrimaryGpu;
        if (gpu is not null)
            sb.AppendLine($"顯示卡：{gpu.Name}　負載 {gpu.LoadText}　溫度 {gpu.TempText}"
                        + $"　核心 {gpu.CoreClockText}　顯示記憶體 {gpu.VramText}");

        if (vm.CudaVersion != "****")
            sb.AppendLine($"CUDA：{vm.CudaVersion}");

        // ── 儲存 ────────────────────────────────────────────────
        var vols = vm.Volumes.Volumes;
        if (vols.Count > 0)
            sb.AppendLine("磁碟區：" + string.Join("　", vols.Select(v => $"{v.CaptionText} {v.SizeText}")));

        if (vm.PhysicalDisks.Count > 0)
            sb.AppendLine("實體磁碟：" + string.Join("　", vm.PhysicalDisks.Select(d => d.Model)));

        // ── 健康總評 ────────────────────────────────────────────
        sb.AppendLine($"健康總評：{vm.Health.ScoreText}　{vm.Health.Summary}");

        return sb.ToString().TrimEnd();
    }
}
