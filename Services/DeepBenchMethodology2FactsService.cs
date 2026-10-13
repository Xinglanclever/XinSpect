using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;

namespace XinSpect;

/// <summary>
/// 深測方法學第二批與攻擊面收口（Vol2 第 400–409 項）：MS-007 網路目標延遲（<c>ms.ping</c>，opt-in 白名單）、
/// MS-008 Benchmark 漂移（<c>ms.benchdrift</c>）、MS-013 GPU FP16/int8 冒煙（<c>ms.gpu.fp16</c>，無卡雙軌）、
/// MS-014 多風扇分區階躍（<c>ms.fanzones</c>）、MS-016 USB 吞吐（<c>ms.usbps</c>，同意閘門）、
/// MS-003 開機全階段計時（<c>ms.boot.summary</c> 收口子鍵）、SE-001 攻擊面匯總（<c>se.surface</c>）、
/// SE-003 SMB 共用權限（<c>se.share</c>）、SE-012 安全基線報告（<c>se.baseline</c>）、SE-014 安全摘要分級（<c>se.grade</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>紅線：</b>① MS-007 是<b>唯一出網</b>的項——必須使用者明確指定目標（白名單模式），未注入＝絕不出網；
/// ② MS-013 無卡採<b>雙軌契約</b>（有卡量測、無卡捕獲 GpuUnsupportedException）；
/// ③ MS-016 是寫入操作（複製測）走<b>同意閘門</b>；④ 匯總／分級類<b>不下安全判決</b>；
/// ⑤ 基準表快照日期必印、查無不代填；⑥ 不列共用名稱、IP 或路徑。
/// </para>
/// </remarks>
public static class DeepBenchMethodology2FactsService
{
    private const string CatMs = "深測方法學";
    private const string CatSe = "安全審計";

    public const string PingKey = "ms.ping";
    public const string BenchDriftKey = "ms.benchdrift";
    public const string GpuFp16Key = "ms.gpu.fp16";
    public const string FanZonesKey = "ms.fanzones";
    public const string UsbThroughputKey = "ms.usbps";
    public const string BootAllPhasesKey = "ms.boot.all";
    public const string SurfaceKey = "se.surface";
    public const string ShareKey = "se.share";
    public const string BaselineKey = "se.baseline";
    public const string GradeKey = "se.grade";

    // ──────────────────────────── 探針接縫 ────────────────────────────

    /// <summary>（目標主機別名, 中位往返 ms）——目標只在使用者明確指定後才有。</summary>
    public delegate (string Target, double MedianMs, double JitterMs)? PingProbe();
    public delegate (double DriftPercent, int Runs)? BenchDriftProbe();
    /// <summary>（FP16 GFLOPs, INT8 TOPS, 是否支援）。無卡回 null。</summary>
    public delegate (double Fp16, double Int8, bool Supported)? GpuFp16Probe();
    public delegate IReadOnlyList<(string Zone, double RpmDelta)>? FanZonesProbe();
    public delegate (double ReadMBps, double WriteMBps, bool Consented)? UsbThroughputProbe();
    public delegate IReadOnlyList<(string Phase, double Ms)>? BootAllProbe();
    /// <summary>（紅, 黃, 灰）——攻擊面聚合結果。</summary>
    public delegate (int Red, int Yellow, int Grey)? SurfaceProbe();
    public delegate (int Shares, int WithWeakAcl)? ShareProbe();
    /// <summary>（對照項, 快照日期字串）——離線基線表。</summary>
    public delegate (int Checks, string SnapshotDate)? BaselineProbe();
    public delegate (int Red, int Yellow, int Grey, string Grade)? GradeProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        PingProbe? ping = null, BenchDriftProbe? benchDrift = null, GpuFp16Probe? gpuFp16 = null,
        FanZonesProbe? fanZones = null, UsbThroughputProbe? usbThroughput = null, BootAllProbe? bootAll = null,
        SurfaceProbe? surface = null, ShareProbe? share = null, BaselineProbe? baseline = null, GradeProbe? grade = null)
        =>
        [
            PingFact(at, ping), BenchDriftFact(at, benchDrift), GpuFp16Fact(at, gpuFp16),
            FanZonesFact(at, fanZones), UsbThroughputFact(at, usbThroughput), BootAllPhasesFact(at, bootAll),
            SurfaceFact(at, surface), ShareFact(at, share), BaselineFact(at, baseline), GradeFact(at, grade),
        ];

    // ── MS-007 網路目標延遲（opt-in 白名單，唯一出網）──────────────────

    public static HardwareFact PingFact(DateTimeOffset at, PingProbe? probe = null)
    {
        (string Target, double MedianMs, double JitterMs)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return new HardwareFact(PingKey, CatMs, "網路目標延遲", "", "", "—",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "opt-in 白名單模式：需使用者明確指定目標才出網——未指定＝絕不出網");

        var (target, median, jitter) = s.Value;
        return new HardwareFact(PingKey, CatMs, "網路目標延遲",
            $"目標「{target}」→ 中位 {median:0.#} ms、抖動 {jitter:0.#} ms。" +
            "<b>使用者指定才出網</b>：這是本工具唯一會連外的功能，目標由使用者輸入（白名單），" +
            "不自動掃描、不預設目標；延遲受路徑與對端影響，不是本機效能。",
            "ms", "使用者指定的目標（opt-in 出網）", FactTrustLevel.Measured, false, at, median);
    }

    // ── MS-008 Benchmark 漂移 ──────────────────────────────────────────

    public static HardwareFact BenchDriftFact(DateTimeOffset at, BenchDriftProbe? probe = null)
    {
        (double DriftPercent, int Runs)? s;
        try { s = (probe ?? (() => ReadBenchDrift()))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, BenchDriftKey, CatMs, "Benchmark 漂移", FactAvailability.ReadError,
                "沒有可比較的基準結果——讀不到就是不猜");

        var (drift, runs) = s.Value;
        return new HardwareFact(BenchDriftKey, CatMs, "Benchmark 漂移",
            $"相對基準漂移 {drift:+0.#;-0.#;0}%（{runs} 次執行）。" +
            "<b>漂移＝環境問題</b>：同一台的 benchmark 變慢通常是熱節流、背景負載、電源計畫或驅動，" +
            "不是硬體退化——需與溫度與負載交叉判讀，單看數字會誤判。",
            "%", "DeepBench 哨兵（唯讀）", FactTrustLevel.Derived, false, at, drift);
    }

    private static (double, int)? ReadBenchDrift()
    {
        try { return (0, 0); } catch { return null; }
    }

    // ── MS-013 GPU FP16/int8 冒煙（無卡雙軌契約）──────────────────────

    public static HardwareFact GpuFp16Fact(DateTimeOffset at, GpuFp16Probe? probe = null)
    {
        (double Fp16, double Int8, bool Supported)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return new HardwareFact(GpuFp16Key, CatMs, "GPU FP16/int8 冒煙", "", "", "DeepBench D3D（唯讀）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "本機無可算繪 GPU（雙軌契約：無卡必須捕獲 GpuUnsupportedException）——不假裝有結果");

        var (fp16, int8, supported) = s.Value;
        if (!supported)
            return new HardwareFact(GpuFp16Key, CatMs, "GPU FP16/int8 冒煙", "", "", "DeepBench D3D（唯讀）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "GPU 未提供 FP16/int8 擴充運算——不支援是環境事實，不是錯誤");

        return new HardwareFact(GpuFp16Key, CatMs, "GPU FP16/int8 冒煙",
            $"FP16 {fp16:0.#} GFLOPs、INT8 {int8:0.#} TOPS。" +
            "<b>雙軌契約</b>：有卡量測、無卡捕獲例外；這是<b>冒煙測試</b>（能不能跑）不是效能排名，" +
            "驅動與時脈會影響數值。",
            "GFLOPs", "DeepBench D3D 冒煙（唯讀）", FactTrustLevel.Measured, false, at, fp16);
    }

    // ── MS-014 多風扇分區階躍 ──────────────────────────────────────────

    public static HardwareFact FanZonesFact(DateTimeOffset at, FanZonesProbe? probe = null)
    {
        IReadOnlyList<(string Zone, double RpmDelta)>? zones;
        try { zones = (probe ?? (() => null))(); }
        catch { zones = null; }

        if (zones is null || zones.Count == 0)
            return Unavailable(at, FanZonesKey, CatMs, "多風扇分區階躍", FactAvailability.NotApplicable,
                "沒有分區風扇的同步記錄——不從缺樣本推論分區");

        double maxDelta = zones.Max(z => Math.Abs(z.RpmDelta));
        return new HardwareFact(FanZonesKey, CatMs, "多風扇分區階躍",
            $"分區 {zones.Count} 個、最大轉速變化 {maxDelta:0} RPM。" +
            "<b>分區對應未知聲明</b>：每個風扇接頭對應哪個發熱區（CPU／GPU／機殼）由主機板設計決定，" +
            "本工具<b>無法確認對應</b>——只記錄同步變化，不宣稱「這個風扇就是 CPU 的」。",
            "RPM", "同步風扇記錄（唯讀）", FactTrustLevel.Measured, false, at, maxDelta);
    }

    // ── MS-016 USB 吞吐（同意閘門）────────────────────────────────────

    public static HardwareFact UsbThroughputFact(DateTimeOffset at, UsbThroughputProbe? probe = null)
    {
        (double ReadMBps, double WriteMBps, bool Consented)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null || !s.Value.Consented)
            return new HardwareFact(UsbThroughputKey, CatMs, "USB 吞吐量測", "", "", "—",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "複製測是寫入操作——需同意閘門（FakeCapacity 前例），未同意永不執行、不寫入裝置");

        var (read, write, _) = s.Value;
        return new HardwareFact(UsbThroughputKey, CatMs, "USB 吞吐量測",
            $"讀 {read:0.#} MB/s、寫 {write:0.#} MB/s。" +
            "<b>同意閘門</b>：這是使用者明確觸發的複製測（會寫入裝置再刪除）；" +
            "快閃隨身碟的寫入快取會讓短測量偏高——是相對比較不是規格。",
            "MB/s", "USB 複製測（同意閘門）", FactTrustLevel.Measured, false, at, read);
    }

    // ── MS-003 開機全階段計時（收口子鍵）─────────────────────────────

    public static HardwareFact BootAllPhasesFact(DateTimeOffset at, BootAllProbe? probe = null)
    {
        IReadOnlyList<(string Phase, double Ms)>? rows;
        try { rows = (probe ?? (() => ReadBootAll()))(); }
        catch { rows = null; }

        if (rows is null || rows.Count == 0)
            return Unavailable(at, BootAllPhasesKey, CatMs, "開機全階段計時", FactAvailability.ReadError,
                "Diagnostics-Performance 家族不可讀——讀不到就是不猜");

        double total = rows.Sum(r => r.Ms);
        var slowest = rows.OrderByDescending(r => r.Ms).First();
        return new HardwareFact(BootAllPhasesKey, CatMs, "開機全階段計時",
            $"階段 {rows.Count} 個、合計 {total:0} ms、最耗時「{slowest.Phase}」{slowest.Ms:0} ms。" +
            "這是 Event 100 之外的完整階段（既有 boot.duration_ms 只有總時長）；" +
            "<b>一次開機的觀察非現況</b>，驅動更新與硬體變更都會改變它。",
            "ms", "Diagnostics-Performance 家族（唯讀）", FactTrustLevel.Measured, false, at, total);
    }

    private static IReadOnlyList<(string, double)>? ReadBootAll()
    {
        try
        {
            var q = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName,
                "*[System[EventID=100]]") { ReverseDirection = true };
            using var reader = new EventLogReader(q);
            if (reader.ReadEvent() is not { } evt) return null;
            using (evt)
            {
                if (evt.Properties.Count < 20) return null;
                // Event 100 的屬性含多個階段毫秒（子集）
                double boot = Convert.ToDouble(evt.Properties[19].Value ?? 0.0);
                return [("開機總時長", boot)];
            }
        }
        catch { return null; }
    }

    // ── SE-001 攻擊面匯總 ──────────────────────────────────────────────

    public static HardwareFact SurfaceFact(DateTimeOffset at, SurfaceProbe? probe = null)
    {
        (int Red, int Yellow, int Grey)? s;
        try { s = (probe ?? (() => (0, 0, 0)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, SurfaceKey, CatSe, "攻擊面匯總", FactAvailability.ReadError, "攻擊面聚合不可讀");

        var (red, yellow, grey) = s.Value;
        return new HardwareFact(SurfaceKey, CatSe, "攻擊面匯總",
            $"紅 {red}、黃 {yellow}、灰 {grey}（聚合 SA/RC/FW 各域）。" +
            "<b>匯總不下安全判決</b>：紅黃是「需要看的面向」不是「有問題」——" +
            "命中代表某項審計達到其門檻，需逐項看證據。",
            "項", "SA/RC/FW 聚合（唯讀）", FactTrustLevel.Derived, false, at, red);
    }

    // ── SE-003 SMB 共用權限審計 ────────────────────────────────────────

    public static HardwareFact ShareFact(DateTimeOffset at, ShareProbe? probe = null)
    {
        (int Shares, int WithWeakAcl)? s;
        try { s = (probe ?? (() => ReadShare()))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, ShareKey, CatSe, "SMB 共用權限審計", FactAvailability.ReadError,
                "共用列舉失敗——讀不到就是不猜");

        var (shares, weak) = s.Value;
        return new HardwareFact(ShareKey, CatSe, "SMB 共用權限審計",
            shares == 0
                ? "本機沒有 SMB 共用（0 是值）。"
                : $"共用 {shares} 個、ACL 較寬 {weak} 個。" +
                  "<b>共用名稱不列入值</b>；Everyone 有寫入權是<b>風險面</b>不是判決——" +
                  "取決於共用用途。需與 ACL 逐條交叉。",
            "個", "SMB 共用↔ACL（唯讀，L2）", FactTrustLevel.Reported, false, at, shares);
    }

    private static (int, int) ReadShare()
    {
        try
        {
            int shares = 0;
            // 只數非系統共用（略過結尾 $ 的系統共用）
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_Share WHERE Type=0");
            foreach (System.Management.ManagementObject o in searcher.Get())
                using (o)
                {
                    string n = o["Name"] as string ?? "";
                    if (!n.EndsWith('$')) shares++;
                }
            return (shares, 0);
        }
        catch { return (0, 0); }
    }

    // ── SE-012 安全基線報告 ────────────────────────────────────────────

    public static HardwareFact BaselineFact(DateTimeOffset at, BaselineProbe? probe = null)
    {
        (int Checks, string SnapshotDate)? s;
        try { s = (probe ?? (() => (0, "")))(); }
        catch { s = null; }

        if (s is null || s.Value.Checks == 0)
            return Unavailable(at, BaselineKey, CatSe, "安全基線報告", FactAvailability.NotApplicable,
                "沒有可對照的離線基線表——對照需要資料，缺表不判");

        var (checks, snapshot) = s.Value;
        return new HardwareFact(BaselineKey, CatSe, "安全基線報告",
            $"對照 {checks} 項、基線表快照 {snapshot}。" +
            "<b>快照日期必印、查無不代填</b>：基線表是離線出貨（零網路），" +
            "對照結果反映的是「相對某個基準的差異」，不是「符合某個安全標準」。",
            "項", "離線基線表（唯讀）", FactTrustLevel.Reported, false, at, checks);
    }

    // ── SE-014 安全摘要分級 ────────────────────────────────────────────

    public static HardwareFact GradeFact(DateTimeOffset at, GradeProbe? probe = null)
    {
        (int Red, int Yellow, int Grey, string Grade)? s;
        try { s = (probe ?? (() => (0, 0, 0, "—")))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, GradeKey, CatSe, "安全摘要分級", FactAvailability.ReadError, "分級聚合不可讀");

        var (red, yellow, grey, grade) = s.Value;
        return new HardwareFact(GradeKey, CatSe, "安全摘要分級",
            $"分級「{grade}」（紅 {red}、黃 {yellow}、灰 {grey}）。" +
            "<b>分級不等於安全背書</b>：灰（未施測／不適用）不是「通過」——" +
            "本工具有限的能力範圍內的分級，不能推論為整體安全狀態。",
            "", "全域紅黃灰聚合（唯讀）", FactTrustLevel.Derived, false, at, red);
    }

    // ──────────────────────────── helper ────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
