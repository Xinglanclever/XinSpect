using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 事實鍵對帳覆蓋的申報（規則覆蓋測試）。這一組不要求「全覆蓋」——那會逼出湊數的規則。
/// 它要保證的是三件事：①每個鍵都被分類到三種狀態之一；②豁免都寫得出理由；
/// ③未覆蓋的數字是<b>已知的</b>，而且會隨事實增加而變動時被看見。
/// </summary>
public class FactCoverageReportTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Rules")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// 從程式碼掃出事實鍵——直接重用 CoverageService 的掃描器（单一来源）。
    /// 這裡原本有一份獨立樣式清單的四條副本，2.45 給生產掃描器補到六條時沒有同步，
    /// 「畫面數字與測試數字會漂移」正是它自己註解警告過的坑；2.49 起改呼叫同一支，
    /// 兩邊不可能再各掃各的。
    /// </summary>
    private static HashSet<string> ScanFactKeys()
        => CoverageService.ScanFactKeysFromSource(Path.Combine(RepoRoot(), "Services"));

    private static List<(string Id, IReadOnlyList<string> Inputs)> LoadRules()
    {
        string json = File.ReadAllText(Path.Combine(RepoRoot(), "Rules", "builtin.json"));
        using var doc = JsonDocument.Parse(json);
        var list = new List<(string, IReadOnlyList<string>)>();
        foreach (var r in doc.RootElement.GetProperty("rules").EnumerateArray())
        {
            string id = r.GetProperty("id").GetString() ?? "";
            var inputs = r.GetProperty("inputs").EnumerateArray()
                .Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
            list.Add((id, inputs));
        }
        return list;
    }

    // ── 分類的完整性 ──────────────────────────────────────────────────────

    [Fact]
    public void 掃到的事實鍵要有意義的數量()
    {
        // 掃描本身若壞掉（正則失效、路徑錯），會得到 0 或極少數——
        // 那會讓下面每一條測試都「通過」，所以先把掃描釘住。
        var keys = ScanFactKeys();
        Assert.True(keys.Count >= 100, $"只掃到 {keys.Count} 個事實鍵——掃描邏輯可能失效了");
    }

    [Fact]
    public void 每個鍵都被分類到三種狀態之一()
    {
        var keys = ScanFactKeys();
        var rules = LoadRules();
        var ruleInputs = rules.SelectMany(r => r.Inputs).ToHashSet(StringComparer.Ordinal);

        foreach (string key in keys)
        {
            var c = FactCoverageReport.Classify(key, ruleInputs);
            Assert.True(Enum.IsDefined(c), $"{key} 沒有被分類");
        }
    }

    [Fact]
    public void 申報的加總要等於總數()
    {
        var s = FactCoverageReport.Report(ScanFactKeys(), LoadRules());
        Assert.Equal(s.Total, s.Covered + s.Exempt + s.Uncovered);
        Assert.Equal(s.Uncovered, s.UncoveredKeys.Count);
    }

    [Fact]
    public void 未覆蓋的鍵要排序且不重複()
    {
        var s = FactCoverageReport.Report(ScanFactKeys(), LoadRules());
        Assert.Equal(s.UncoveredKeys.OrderBy(x => x, StringComparer.Ordinal), s.UncoveredKeys);
        Assert.Equal(s.UncoveredKeys.Count, s.UncoveredKeys.Distinct().Count());
    }

    // ── 豁免的品質 ────────────────────────────────────────────────────────

    [Fact]
    public void 每一條豁免都要寫得出理由()
    {
        var bad = FactCoverageReport.ExemptionsWithoutReasons();
        Assert.True(bad.Count == 0,
            "以下豁免沒有寫出理由（沒有理由的豁免等於沒豁免）：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void 豁免不得是空字串前綴_那會豁免掉全部()
    {
        Assert.DoesNotContain("", FactCoverageReport.Exemptions.Keys);
    }

    [Fact]
    public void 豁免的前綴要真的用得到()
    {
        // 沒用到的豁免會讓申報看起來比實際乾淨——那是另一種形式的說謊
        var keys = ScanFactKeys();
        var unused = FactCoverageReport.Exemptions.Keys
            .Where(prefix => !keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))).ToList();
        Assert.True(unused.Count == 0, "以下豁免前綴沒有對應的事實鍵——登記了卻沒人用是另一種謊：" + string.Join("、", unused));
    }

    // ── 未覆蓋的數字必須被看見 ────────────────────────────────────────────

    [Fact]
    public void 申報措辭不得說成通過或失敗()
    {
        var s = FactCoverageReport.Report(ScanFactKeys(), LoadRules());
        Assert.Contains("尚未被任何規則碰到", s.Headline);
        Assert.DoesNotContain("通過", s.Headline);
        Assert.DoesNotContain("失敗", s.Headline);
    }

    [Fact]
    public void 未覆蓋數超過已知基線時_列出前幾個供人核對()
    {
        // 這條不是禁止未覆蓋（那會逼出湊數的規則），而是讓它「有上限地增長」：
        // 事實從 300 多個長到某個程度時要有人看一眼，而不是靜默累積。
        //
        // 2026-10-17 檢視過一次（未覆蓋 307，逐族核完）：**沒有動豁免表**，原因是
        // FactCoverageReport.Exemptions 已經是 600 條、正好在 KeyCatalogPolicyTests 的 cap
        // 上（超過會判 Unknown）——再進一條就得先刪既有條目，而那些條目裡有的是「刻意不取得
        // 來源」（例如 SRUM 不列行程與帳號），用「單一來源」的家族級豁免去覆蓋會把類別標錯。
        // 所以上限改成「已檢視的那個數字」，而不是把 307 條擠成豁免。
        // **可以交叉、只是還沒寫規則**的家族（補規則時把它們往下帶）：
        //   sb.、fw.、uefi.、bt.、mc.、spi.、cpu.、bl.、ev.、ec.、rs.，
        //   以及六個原本就列為「有硬體時應補規則」的：gpu.／mem.／psu.pmbus.／smart.／tpm.／cmos.
        // 其餘各族的來源，與為什麼沒有交叉對象，逐族寫在
        // docs/ULTIMATE-1000/GATE-LOG-257.md 的第三節。
        //
        // 2026-10-17 同日的第二批：+2 條（`tpm.owned`＝P2-069、`tpm.attest.capable`＝P2-075），
        // 兩個都在**刻意不豁免的 tpm. 那一族**裡（可以對：Windows 的 Win32_Tpm 視圖、
        // 量測開機日誌），所以照上面的規矩留未覆蓋，上限跟著移。
        // 2026-10-17 第三批：+1 條（`pdev.fw.version`＝P2-096 周邊韌體版本盤點）。
        // 它落在**刻意不豁免的 pdev. 那一族**裡——可以對的對象寫在服務檔的類別註解
        //（硬碟自述的韌體版本 vs 原廠清單／網卡在 Windows 沒有韌體欄位），
        // 所以照上面的規矩留未覆蓋，上限跟著移。同批的三把 uefi.*.count 不在此列——
        // 它們是掃描器原本看不到的字面（見 UefiSignatureFactsService 的常數註解），
        // 登記進目錄後才進入掃描面，因此這一條的基線變化要分開記。
        //
        // 2026-10-18 第四批：+3 條（`sb.memmap.count`／`sb.memmap.total`＝P2-024 Type 19 逐段映射、
        // `ec.thermal.readable`＝P2-094 熱管理可讀項）。三把都在**刻意不豁免的家族**裡
        //（前者可以對 Type 16／17 與 OS 看到的記憶體，後者可以對 SMBIOS 探針與 WMI 熱區），
        // 所以照規矩留未覆蓋，上限跟著移到 316。
        //
        // 2026-10-18 第五批：+1 條（`fw.doc.reconcile`＝P2-105 階段二文件對帳）。
        // 它陳述的是「階段二矩陣對實作」這一層對帳的結果本身，**沒有第二個來源可以再交叉一次**
        // ——但豁免表已經在上限（KeyCatalogPolicyTests 的 600 條，再加一條就會紅），
        // 而那個上限的意思是「該檢視豁免了，不是再提高上限」。所以照規矩留在未覆蓋，
        // 上限跟著移到 317；守門在 FirmwareDocReconcileTests（逐列重算矩陣）。
        //
        // 2026-10-18 第六批：+3 條（`ev.var.count`／`fw.capsule.history`／`ev.nvram.usage`
        // ＝P2-082／089／090 EFI 變數面三把）。三把的共同結論是「Windows 沒有列舉 EFI 變數的
        // 公開 API」，所以**它們連「拿兩個來源對一次」的前提都沒有**——沒有列舉就沒有第二個
        // 視圖可比。上限跟著移到 320；同一批的論述在 GATE-LOG-261 第三節。
        // 2026-10-18 第七批：+6 條（`fw.risk.facts.*`＝P2-099～P2-104 韌體風險陳述）。
        // 這六把的**輸入**是既有事實聯集，但「等級／缺口／基線／差異」本身是對那批事實的**彙總陳述**：
        // 要拿它們去交叉，就得先有第二套獨立的彙總——**第二套彙總不是第二個來源，只是第二份實作**
        // （兩份都會漂）。所以照規矩留在未覆蓋，上限跟著移到 326。
        // 它們的內部一致性由 Tests/FirmwareRiskFactsTests 守著（等級只由有沒有決定、缺口不當 0、
        // 基線不含時間、差異只比參與鍵）。
        // 2026-10-18 第八批：+1 條（`pdev.fw.updatable`＝P2-098 周邊韌體可更新性宣告）。
        // 它讀的是**裝置自述的韌體槽結構**（NVMe 紀錄頁 0x03），而「可更新」這個判斷**不是讀出來的**
        // ——本專案沒有第二條路可以獨立問同一台裝置「你覺得自己能不能更新」，所以無從交叉。
        // 上限跟著移到 327；ATA 半邊如實標缺源，守門在 Tests/FirmwareUpdatableFactsTests。
        // 2026-10-25 第九批：+12 條（U10 檢測域第一批唯讀檢測，`os.memdiag.results.cross` 與
        // `ts.disp/touch/kbd/clock/reg/svc/aud.pen` 十一把基底鍵）。這些是**單一來源的檢測量測**：
        // 問的是「這台機器的檢測結果」，沒有第二條獨立通路可以再問一次同一個問題
        //（格式支援枚舉、插孔描述、時鐘漂移、登錄檔列舉延遲……各自的來源寫在服務檔頭註解）；
        // 豁免表已在上限（KeyCatalogPolicyTests 的 600 條），照規矩留未覆蓋，上限跟著移到 339。
        // 逐項來源與界線：Services/DeepTest/ 各服務的類別註解。
        // 2026-10-25 第 269 輪：+7 條（`tpm.view.cross`＝TBS×WMI 兩視圖對照、`uefi.dbt.count`／
        // `uefi.dbr.count`＝兩個同構簽章資料庫、`uefi.boot.gaps`＝Boot 條目跳號、`ac.tcpa`／
        // `ac.tcpa.cross`＝TCPA 表與 TPM2 並存、`pdev.fw.surface`＝周邊韌體盤點彙整）。
        // 全部是「單一來源的陳述」——對照鍵的兩側都來自同一輪讀取（不是兩個獨立來源），
        // 彙整鍵的數字對回盤點本身；沒有可交叉的第二通路，照規矩留未覆蓋，上限跟著移到 346。
        // 2026-10-25 第 270 輪：+8 條（`mem.mmio.total`＝MMIO 洞盤點、`sb.type45`／`sb.type45.cross`
        // ＝SMBIOS Type 45、`fw.fv.cross`＝FV 分解現況、`tpm.ev.types`＝TCG log 事件類型統計、
        // `pdev.fw.inventory`＝全裝置韌體版本盤點、`sec.vbs.state`／`sec.vbs.required`＝VBS 狀態與
        // 原因面）。同為單一來源陳述（彙整鍵的數字對回盤點本身），照規矩留未覆蓋，上限跟著移到 353
        //（+8 中 1 把落在既有家族豁免內，實測 346+7）。
        // 2026-10-25 第 271 輪：+11 條（`mem.wmi.cross`＝SPD 審計 WMI 第四源、`net.wmi.gap`＋
        // 四個固定子鍵＝Configuration 面未取欄位、`sec.logon.hw.correlate`＝登入×裝置交集
        // （只出次數，帳號面遮蔽）、`os.wmi.fields`＋逐類家族＝WMI 未用欄位盤點、
        // `ata.smart.attr.lookup`＝SMART 知識表查詢統計（本輪如實申報「未被查詢」））。
        // 同為單一來源／治理成文陳述，照規矩留未覆蓋，上限跟著移到 365（+11 中 1 把被既有規則覆蓋，實測 353+12）。
        // 2026-10-25 第 272 輪：+4 條（`ts.tsc.skew.max`＝TSC 跨核偏移、`ui.home.healthcards`＝
        // 首頁健康摘要卡、`rep.diff.mode`＝報告 diff、＋第二圈其餘新鍵）。自負載檢測的逐項鍵走
        // 動態家族（ts.vector.flops. 等），彙總與對照鍵留未覆蓋，上限跟著移到 368。
        // 2026-10-25 第 273 輪：+11 條（`exp.json.schema`／`exp.partial`＝匯出 schema 版本化與
        // 選擇性匯出、`rep.sec.cover`／`rep.sec.summary`＝報告封面與摘要節、`ui.keys.map`＝
        // 快捷鍵表、`ui.table.controls`＝表格排序、`ui.dashboard.sensors`＝感測器儀表板頁、
        // `viz.heatmap.coretime`／`viz.bullet`／`viz.calendar.heatmap`／`viz.controlchart`＝
        // 四個自繪控制項的追蹤鍵）。呈現層與匯出層的追蹤鍵，照規矩留未覆蓋，上限跟著移到 379。
        // 2026-10-25 第 274 輪：+2 條（`ev.pack.build`＝證據包建立追蹤鍵、`ata.smart.attr.lookup`
        // 從「以空列集申報未被查詢」改成讀真的 ATA SMART 屬性列——同一把鍵，值面升格）。
        // 另 `ev.manifest`／`ev.chain.verify` 從「只有測試在呼叫」變成由 `--evidence-pack` 生產，
        // 鍵本來就在目錄裡（不新增列），值面升格。上限跟著移到 381。
        var s = FactCoverageReport.Report(ScanFactKeys(), LoadRules());
        Assert.True(s.Uncovered <= 381,
            $"未覆蓋的事實鍵增至 {s.Uncovered} 個（已檢視基線 381）。新增事實時請順手評估要不要納入對帳，"
            + $"或加進 FactCoverageReport.Exemptions 並寫明理由。前 20 個未覆蓋：\n"
            + string.Join("\n", s.UncoveredKeys.Take(20)));
    }

    [Fact]
    public void 已覆蓋數不得低於規則引用的相異鍵數()
    {
        // 規則引用的鍵一定要被算成已覆蓋——否則分類邏輯有問題
        var rules = LoadRules();
        var s = FactCoverageReport.Report(ScanFactKeys(), rules);
        Assert.True(s.Covered >= s.RuleInputCount - s.Uncovered,
            $"已覆蓋 {s.Covered} 少於規則引用的 {s.RuleInputCount}——分類可能沒把規則輸入算進去");
    }

    [Fact]
    public void 規則檔本身要讀得到且條數合理()
    {
        var rules = LoadRules();
        Assert.True(rules.Count >= 20, $"只讀到 {rules.Count} 條規則——規則檔可能壞了");
        Assert.All(rules, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Id));
            Assert.NotEmpty(r.Inputs);
        });
    }
}
