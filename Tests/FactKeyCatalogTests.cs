using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 事實鍵目錄與原始碼的一致性，以及<b>「掃不到」不得冒充「沒有問題」</b>。
/// </summary>
/// <remarks>
/// 這一組存在的理由是一個已發佈的缺陷：覆蓋申報原本在執行期掃 <c>Services/</c> 原始碼，
/// 而發佈版是單一 exe、沒有那個目錄——掃不到時回報「0 個鍵」，
/// 申報再把 0 讀成「所有事實鍵都已被規則考慮或明文豁免」。
/// <b>畫面顯示一個肯定的結論，那個結論是空的。</b>
/// </remarks>
public class FactKeyCatalogTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Services")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ── 目錄與原始碼一致 ──────────────────────────────────────────────────

    [Fact]
    public void 目錄與原始碼掃描逐鍵相等()
    {
        var scanned = CoverageService.ScanFactKeysFromSource(Path.Combine(RepoRoot(), "Services"));
        Assert.True(scanned.Count > 0, "原始碼掃描回空集合——掃描邏輯或路徑壞了");

        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);

        var missing = scanned.Except(catalog).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var extra = catalog.Except(scanned).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0 && extra.Count == 0,
            "事實鍵目錄與原始碼不一致——新增或移除事實後請更新 Services/FactKeyCatalog.cs。\n"
            + $"原始碼有而目錄沒有（{missing.Count}）：{string.Join("、", missing.Take(20))}\n"
            + $"目錄有而原始碼沒有（{extra.Count}）：{string.Join("、", extra.Take(20))}");
    }

    [Fact]
    public void 目錄不得有重複鍵且要排序()
    {
        Assert.Equal(FactKeyCatalog.Keys.Count, FactKeyCatalog.Keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(FactKeyCatalog.Keys.OrderBy(x => x, StringComparer.Ordinal), FactKeyCatalog.Keys);
        Assert.Equal(FactKeyCatalog.Keys.Count, FactKeyCatalog.Count);
    }

    /// <summary>
    /// 排序壞掉時要<b>指名</b>是哪幾把鍵、錯在第幾位——排序斷言原本只回一句「集合不相等」，
    /// 於是 2026-10-15 那次錯位只留下「目錄紅燈、重排後綠」的記憶，<b>連錯了幾把都說不出來</b>，
    /// 事後註解裡的數字無從對帳。守門的訊息本身就是證據，不要讓它只說「紅了」。
    /// </summary>
    [Fact]
    public void 目錄排序壞掉時要指名錯位的鍵與位置()
    {
        var offenders = FactKeyCatalog.Keys
            .Select((k, i) => (Key: k, Index: i))
            .Where(x => x.Index > 0
                     && string.CompareOrdinal(FactKeyCatalog.Keys[x.Index - 1], x.Key) > 0)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"事實鍵目錄有 {offenders.Count} 處逆序（相鄰兩鍵前者大於後者）："
            + string.Join("、", offenders.Take(10).Select(o =>
                $"[{o.Index - 1}] {FactKeyCatalog.Keys[o.Index - 1]} > [{o.Index}] {o.Key}")));
    }

    [Fact]
    public void 目錄鍵要符合事實鍵格式()
    {
        foreach (string k in FactKeyCatalog.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(k));
            Assert.Matches(@"^[a-z][a-z0-9_]*(\.[a-z0-9_]+)*$", k);
        }
    }

    /// <summary>
    /// 動態鍵家族目錄的殭屍前綴守門：每個登記的前綴，其字面值必須在 Services/ 原始碼掃得到——
    /// 「登記了家族但沒人生產那個前綴」跟「目錄有鍵但沒人生產」是同一种謊，只是粒度粗一點。
    /// 上限守門同時在這一條裡（超過就逼著重新數動態面）。
    /// </summary>
    [Fact]
    public void 動態鍵家族的每個前綴都要真的有人在原始碼裡生產()
    {
        string services = Path.Combine(RepoRoot(), "Services");
        var text = new System.Text.StringBuilder();
        foreach (string file in Directory.EnumerateFiles(services, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
             || Path.GetFileName(file) == "FactKeyDynamicCatalog.cs") continue;   // 登记表自己不當證據
            try { text.Append(File.ReadAllText(file)); } catch (IOException) { }
        }
        string corpus = text.ToString();

        // 上限是「回頭數一數」的觸發器，不是常數：每次提高都必須說出那幾個新家族為什麼
        // 真的無法靜態枚舉。16 → 18（2026-10-11）：audio.latency.（逐端點延遲樣本，端點數由
        // MMDevice 列舉決定）與 sa.lsp.（Winsock 分層服務提供者逐條，由機器安裝事實決定）。
        // 同日再加的 bl.baseline.／se.cve.hit.／sg.* 逐項明細全部收斂回彙總值裡，沒有動到上限。
        // 18 → 19（2026-10-11）：net.listen.（監聽埠逐埠一筆）——埠集由連線表決定，
        // 是機器狀態不是編譯期常量；net.listen.count/entries/unverified 三把彙總鍵仍在靜態目錄。
        // 19 → 20（2026-10-12）：acpi.table.（韌體提供的 ACPI 表逐張一筆，鍵＝簽章／同簽章補序號）——
        // 有幾張、哪些簽章是機器韌體的事實，編譯期列不完；彙總鍵 acpi.tables／ac.tables.summary 仍在靜態目錄。
        // 這批無主鍵是 v2.67 把 ACPI 列舉移進 LoadUsermodeFacts（GUI 與 CLI 同源）後，執行期對帳立刻照出來的。
        // 20 → 21（2026-10-14）：in.deps.（已載入模組逐條一筆，檔名任意、只有被判「值得看」的才產鍵）——
        // 把逐條模組從彙總值字串裡拉出來成獨立鍵，是為了讓「哪幾個模組沒簽章」能被單獨選取與匯出；
        // 成員集由這個行程當下載入了什麼決定，編譯期列不完。
        // 21 → 22（2026-10-14）：dl.dll.（逐行程模組明細，行程名任意、僅 L2 同意時產鍵）——
        // 與 in.deps. 同一個理由：明細要能被單獨選取／匯出，而成員集由機器當下的行程決定。
        // 22 → 23（2026-10-14）：tpm.replay.pcr.（開機鏈重放逐 PCR 一筆）——tpm.replay.pcr.sha256.{index}
        // 早就以執行期鍵的形式在產，卻一直沒有家族登記，目錄覆蓋掃描把它照成「FW-002 未實作」的假缺口；
        // 補登記之後 FW-002 的 `tpm.replay.*` 才對得上。該跑哪幾條 PCR 由 TCG log 與 TPM 決定。
        // 23 → 26（2026-10-17）：uefi.db.list.／uefi.dbx.list.／uefi.kek.list.（UEFI 簽章資料庫的
        // 逐條清單，最多列 5 條）——有幾條清單是那個變數的內容決定的，編譯期列不完；
        // 這次不是「新功能順手加登記」，而是這三組鍵**早就以執行期鍵的形式在產**卻沒有登記
        //（`keyPrefix + ".list.{i}"`，掃描器只認字面所以一直沒被照到），
        // 由 EfiVariableFactsTests.執行期產生的鍵都要被目錄或動態家族收錄 照出來的。
        // 同一批的「條數」鍵（uefi.db.count／dbx／kek）成員固定，走靜態目錄而不是家族。
        // 26 → 27（2026-10-18）：sb.memmap.（SMBIOS Type 19 逐段映射，P2-024）——
        // 有幾段是韌體的事實（消費級平台常常一段都不列），編譯期列不完；
        // 彙總鍵 sb.memmap.count／sb.memmap.total 成員固定，走靜態目錄。
        // 27 → 28（2026-10-18）：uefi.boot.（UEFI 開機條目逐條一筆）。
        // 它不是新功能帶來的，而是**既有執行期鍵第一次真的被產生出來**：先前權限沒啟用、
        // db／dbx 的 GUID 也錯，這一批從來沒有鍵可對帳；修好之後真機讀到 9 個條目，
        // 執行期對帳就照出了 uefi.boot.0…9 這批無主鍵（見 GATE-LOG-261 第六節）。
        // 同批的彙總鍵 uefi.boot_entries.count 成員固定，走靜態目錄。
        // 28 → 34（2026-10-25）：U10 檢測域（U10-TS）第一批唯讀檢測六個家族
        //（ts.disp.refresh.verify.／ts.disp.modes.cycle.／ts.aud.formats.／ts.aud.jackdetect.／
        // ts.aud.endpoint.state.／ts.pen.pressure.）——成員＝端點／顯示裝置／筆裝置索引，
        // 有幾個是機器環境的事實（與 audio.endpoint.／mon. 同形）；各基底鍵成員固定、仍在靜態目錄。
        // 36 → 37（2026-10-25）：mem.encryption.sgx.epc.（SGX EPC 逐區，U10-CP-0148）——
        // 有幾個 EPC 區由 CPUID leaf 0x12 列舉決定；彙總值寫在 mem.encryption.sgx。
        // 37 → 41（2026-10-25 第 270 輪）：mem.mmio.hole.／sb.type45.item.／tpm.ev.type.／pdev.fw.inv.——
        // 成員＝映射洞序號／Type 45 組件序號／log 實際出現的事件類型／碟序，都是機器事實。
        // 41 → 42（2026-10-25 第 271 輪）：os.wmi.fields.（WMI 未用欄位盤點逐類，成員固定、彙總在靜態目錄）。
        // 42 → 47（2026-10-25 第 272 輪）：ts.vector.flops.／ts.cache.bw.／ts.copy.kernel.／ts.tsc.skew.core.／
        // ts.core.uniformity.core.——成員由檢測矩陣與拓撲決定。
        // 47 → 48（2026-10-27 第 276 輪）：edid.cta.vic.——成員＝顯示器在視訊資料塊裡實際宣告的 VIC；
        // 那是「接了哪一台螢幕」的事實，本機兩台顯示器一個宣告 17 個、一個一個都沒宣告，編譯期列不完。
        // 同批的修訂號／4:2:0／VCDB／HDMI VSDB／HDR／色度學九把成員固定，走靜態目錄。
        // 48 → 49（2026-10-26 第 283 輪）：kp.builtin.item.——成員＝內建知識表實際有幾張（目前九張）；
        // 再加一張知識表就自動多一把鍵，編譯期不該為此改目錄。三把彙總鍵 kp.builtin.{tables,entries,oldest} 走靜態目錄。
        const int FamilyCap = 52;
        Assert.True(FactKeyDynamicCatalog.Families.Count <= FamilyCap,
            $"動態鍵家族已登記 {FactKeyDynamicCatalog.Families.Count} 個——動態面膨脹到這個程度，先回頭數一數哪些其實可以靜態枚舉");

        var zombies = FactKeyDynamicCatalog.Families
            .Where(kv => !corpus.Contains($"\"{kv.Key}", StringComparison.Ordinal)
                      && !corpus.Contains($"$\"{kv.Key}", StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToList();
        Assert.True(zombies.Count == 0,
            "以下動態前綴在 Services/ 原始碼掃不到任何字面——要嘛是殭屍登記（刪掉），要嘛寫法改了要更新登記：" +
            string.Join("、", zombies));

        foreach (var (prefix, reason) in FactKeyDynamicCatalog.Families)
            Assert.True(reason.Trim().Length >= 20, $"{prefix}：家族理由不夠清楚（為什麼动态＋成員由什麼決定）");
    }

    // ── 缺陷的迴歸測試：掃不到不得變成「沒有問題」 ────────────────────────

    [Fact]
    public void 沒有事實鍵時_申報要說無法申報而不是全部覆蓋()
    {
        // 這是已發佈缺陷的核心：Total=0 時第一版顯示「所有事實鍵都已被規則考慮或明文豁免」
        var summary = FactCoverageReport.Report(new HashSet<string>(), []);
        string detail = CoverageService.BuildDetail(summary);

        Assert.Contains("無法申報", detail);
        Assert.Contains("不代表沒有未覆蓋的鍵", detail);
        Assert.DoesNotContain("所有事實鍵都已被規則考慮或明文豁免", detail);
        Assert.DoesNotContain("全部都已覆蓋", detail);
    }

    [Fact]
    public void 沒有規則時_申報要說無法申報()
    {
        var summary = FactCoverageReport.Report(FactKeyCatalog.Keys, []);
        string detail = CoverageService.BuildDetail(summary);

        Assert.Contains("無法申報", detail);
        Assert.Contains("不代表沒有未覆蓋的鍵", detail);
    }

    [Fact]
    public void 掃不到的目錄_回空集合而不是丟例外()
    {
        var keys = CoverageService.ScanFactKeysFromSource(Path.Combine(Path.GetTempPath(), "不存在-" + Guid.NewGuid()));
        Assert.Empty(keys);
    }

    // ── 執行期資料來源正確 ────────────────────────────────────────────────

    [Fact]
    public void 申報的規則來源是內建規則而不是外部檔案()
    {
        // 生產路徑跑的是 FactRelationRules.All；外部 JSON 沒有隨程式出貨。
        // 若申報去讀那個檔，會得到 0 條規則，進而給出假結論。
        var rules = CoverageService.RuntimeRules();
        Assert.Equal(FactRelationRules.All.Count, rules.Count);
        Assert.All(rules, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Id));
            Assert.NotEmpty(r.Inputs);
        });
    }

    [Fact]
    public void 用目錄與內建規則算出的申報_覆蓋數不得為零()
    {
        var s = FactCoverageReport.Report(FactKeyCatalog.Keys, CoverageService.RuntimeRules());
        Assert.True(s.Total > 0, "目錄是空的");
        Assert.True(s.RuleCount > 0, "規則數是 0");
        Assert.True(s.Covered > 0, $"已覆蓋數為 0——{s.RuleCount} 條規則的輸入鍵一個都沒對上目錄");
    }

    // ── 規則來源的申報 ────────────────────────────────────────────────────

    [Fact]
    public void 規則來源申報要說出內建幾條()
    {
        string text = RuleSourceReport.Describe();
        Assert.Contains($"內建 {RuleSourceReport.RuntimeRuleCount} 條", text);
        Assert.Contains("生產路徑實際使用的是這一份", text);
    }

    [Fact]
    public void 外部規則檔不存在時_申報要如實說沒有出貨()
    {
        // 測試環境的 BaseDirectory 下沒有 Rules/ ——與發佈版一致
        if (RuleSourceReport.ExternalRuleFilePresent) return;   // 有出貨時這條不適用

        string text = RuleSourceReport.Describe();
        Assert.Contains("沒有隨程式出貨", text);
        Assert.Contains("不是發佈版的功能", text);
    }

    [Fact]
    public void 規則來源申報不得宣稱外部形式可用_除非它真的在()
    {
        string text = RuleSourceReport.Describe();
        if (!RuleSourceReport.ExternalRuleFilePresent)
            Assert.DoesNotContain("可編輯、可分享）位於", text);
    }
}
