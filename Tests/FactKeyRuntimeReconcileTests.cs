using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 目錄／掃描／執行期三方對帳（主綱 §5.10／T1-4）：
/// 執行期真正產生的每一把鍵，要嘛是 <see cref="FactKeyCatalog"/> 的全鍵、
/// 要嘛命中 <see cref="FactKeyDynamicCatalog"/> 登記過的動態家族、
/// 要嘛在 <see cref="ConditionalOnly"/> 說清楚只在哪个互動流程存在——
/// 三者之外出現的鍵，就是「天天在跑、申報完全看不見」的缺口。
/// 反方向同樣成立：目錄宣稱的鍵若這次沒生產、又沒有互動條件，紅燈。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼敢在測試裡跑真收集：</b>整條路徑唯讀（WMI／CPUID／MSR 讀／MMIO 讀／ACPI／註冊表），
/// 驅動後端未就緒時各服務照規格產三態條目——環境差異改變值與可用性，不該改變「鍵在不在」。
/// </para>
/// <para>
/// <b>2026-10-10 這一條第一次上線就抓到 137 把無主鍵</b>（pci.dev.／reconcile.／sio.hwm. 等）——
/// 當時的收錄方式就是現在這份動態目錄；新出现的無主鍵會再次紅燈指名。
/// </para>
/// </remarks>
/// <summary>對帳要跑真收集——與任何並行測試共享 WMI／MMDevice／驅動會話會互相干擾，整組禁並行。</summary>
[CollectionDefinition("RealHardwareReconcile", DisableParallelization = true)]
public class RealHardwareReconcileCollection
{
}

[Collection("RealHardwareReconcile")]
public class FactKeyRuntimeReconcileTests
{
    /// <summary>只在互動／特定流程產生的全鍵：鍵 → 為什麼啟動管線不產生。</summary>
    private static readonly Dictionary<string, string> ConditionalOnly = new(StringComparer.Ordinal)
    {
        ["spi.compare"] =
            "BIOS 區與參考映像的比對結果——只在使用者按下「與參考映像比對」或 CLI --compare-flash 時存在；" +
            "啟動管線沒有參考映像，不該假裝比對過",
        ["spi.entropy.regions"] =
            "SPI 熵圖的區域描述——熵掃描屬手動觸發的深度檢查，預設啟動管線不跑（體積與時間成本）",
    };

    private const int ConditionalCap = 12;

    private static EvidenceLabService LoadBothEntries()
    {
        var svc = new EvidenceLabService();
        // 儲存收口的 NVMe 探針在這裡走注入：本條驗的是「執行期產出的鍵都在目錄裡」，
        // 不是磁碟回應速度。真碟的紀錄頁查詢會卡在驅動裡好幾秒（離線的介面卡尤其），
        // 那會讓這條對帳測試卡到連結果都印不出來。鍵與三態照樣由服務產生，對帳不受影響。
        using var readers = StorageAndScenarioReconciliationFactsService.UseReaders(new SyntheticStorageReaders());
        EvidenceCollection.ReloadInto(svc);          // 驅動後端組
        EvidenceCollection.LoadUsermodeFacts(svc);   // usermode 組
        svc.LoadPlatformFacts();                     // 平台全組（App 啟動序列的第三條入口）
        return svc;
    }

    /// <summary>對帳用的合成儲存讀取器：值固定，鍵與三態照規格產生。</summary>
    private sealed class SyntheticStorageReaders : StorageAndScenarioReconciliationFactsService.IReaders
    {
        public IReadOnlyList<StorageAndScenarioReconciliationFactsService.WearSample>? Wear() =>
            [new(7, 512.5, 1424, 100)];

        public (ulong UnsafeShutdowns, ulong PowerCycles)? PowerLoss() => (5, 200);

        public bool? Sanitize() => null;
    }

    [Fact]
    [Trait("Category", "RealHardware")]
    public void 執行期每把鍵都要被目錄或動態家族或條件白名單收錄()
    {
        var svc = LoadBothEntries();

        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var runtime = svc.AllFacts.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

        var homeless = runtime
            .Where(k => !catalog.Contains(k) && !ConditionalOnly.ContainsKey(k) && !FactKeyDynamicCatalog.Matches(k))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(homeless.Count == 0,
            "執行期產出了無主鍵——覆蓋申報與目錄都看不見它們：" + string.Join("、", homeless) +
            "\n三選一：收進 FactKeyCatalog（靜態可枚舉）、登記進 FactKeyDynamicCatalog（動態家族＋成員由什麼決定）、" +
            "或 ConditionalOnly（互動才產生＋條件）。");
    }

    /// <summary>
    /// <b>每一個非 Present 的事實都要說得出為什麼（第 287 輪）。</b>
    ///
    /// <para>
    /// 六態的整個理由是「不知道」不能長得像「量到 0」。但只有狀態碼而沒有理由時，
    /// 畫面上一樣只看到「不適用」三個字——使用者無從判斷那是「這台機器沒有這個介面」
    /// 還是「我們讀失敗了」。這條規則是本專案的核心誠實約定，卻一直只靠**寫的人記得**：
    /// 匯出快照那條路徑會驗，但驗的是「非 Present 不得帶數字」，不是「有沒有寫理由」。
    /// </para>
    ///
    /// <para>
    /// 這裡跑真收集（與目錄對帳同一組入口），逐筆檢查。紅燈訊息直接列出是哪幾把鍵——
    /// 一把鍵沒有理由，就是一個使用者無法解讀的狀態。
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "RealHardware")]
    public void 每一個非Present的事實都要說得出為什麼()
    {
        var svc = LoadBothEntries();

        var silent = svc.AllFacts
            .Where(f => f.Availability != FactAvailability.Present)
            .Where(f => string.IsNullOrWhiteSpace(f.UnavailableReason))
            .Select(f => $"{f.Key}（{f.Availability}）")
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(silent.Count == 0,
            $"有 {silent.Count} 筆事實標了「不是這樣」卻沒說為什麼——"
            + "使用者看到的是狀態碼，不是原因：\n" + string.Join("\n", silent.Take(30)));
    }

    /// <summary>
    /// <b>事實集的基本形狀（第 287 輪）。</b>每一筆 `HardwareFact` 都要能被人拿去做判斷，
    /// 所以四件事都不能空：鍵、名稱、來源、量測時刻。
    ///
    /// <para>
    /// <b>為什麼「來源」是硬條件：</b>這個專案的每一個數字都宣稱自己可回溯——
    /// 而回溯的起點就是 <c>Source</c>（哪條通路讀到的）。<b>沒有來源的事實，
    /// 讀者無從判斷它是不是機器說的</b>，只能選擇相信或不信，那不是可稽核。
    /// 名稱空著，畫面上就是一條沒有標籤的值；量測時刻是 <c>default</c> 時，
    /// 「這一輪讀的」與「很久以前讀的」長得一樣。
    /// </para>
    ///
    /// <para>
    /// 鍵的格式另外由 <see cref="KeyCatalogPolicy.CheckKeyName"/> 判（動態家族成員自成一套規則），
    /// 這裡不重寫一份規則。
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "RealHardware")]
    public void 事實集的鍵名稱來源與時刻都不得留空()
    {
        var svc = LoadBothEntries();
        var problems = new List<string>();

        foreach (var f in svc.AllFacts)
        {
            if (string.IsNullOrWhiteSpace(f.Key)) problems.Add("（有一筆事實的鍵是空的）");
            if (string.IsNullOrWhiteSpace(f.Name)) problems.Add($"{f.Key}：名稱是空的");
            if (string.IsNullOrWhiteSpace(f.Source)) problems.Add($"{f.Key}：來源是空的——這一筆無法回溯");
            if (f.MeasuredAtUtc == default) problems.Add($"{f.Key}：量測時刻是 default");
            // NaN／Infinity 會讓 JSON 匯出變成非法文件（JSON 沒有這兩個字面值），
            // 而它們最常見的來源是「除以零的衍生量」——那正是這一層該攔住的地方。
            if (f.NumericValue is { } v && (double.IsNaN(v) || double.IsInfinity(v)))
                problems.Add($"{f.Key}：數值是 {v}（NaN／Infinity 會讓匯出檔變成非法 JSON）");
            if (KeyCatalogPolicy.CheckKeyName(f.Key) is { } bad) problems.Add($"{f.Key}：{bad}");
        }

        Assert.True(problems.Count == 0,
            $"事實集有 {problems.Count} 處基本形狀不合格（每一處都是一筆無法被讀者判斷的輸出）：" + Environment.NewLine
            + string.Join(Environment.NewLine, problems.Distinct(StringComparer.Ordinal).Take(30)));
    }

    /// <summary>
    /// 已接線入口的核心鍵反向抽查：usermode 組的六支服務在 2.43／2.45 接線時都承諾
    /// 「讀不到由服務標三態、不是不產」——所以它們的鍵在兩個入口跑完後必須存在。
    /// 這一組是接線的回歸網：鍵消失＝服務從 AllFacts 串接裡掉出去了（改動時最容易犯）。
    /// 目錄裡其餘的鍵屬 App 頁面層（GPU／顯示／SMBIOS／TPM……），由各自的載入點負責，
    /// 不在这兩個入口的宣稱範圍內——掃描器已經證明它們的生產點存在。
    /// </summary>
    [Fact]
    [Trait("Category", "RealHardware")]
    public void 已接線入口的核心鍵執行期必須存在()
    {
        var svc = LoadBothEntries();
        var runtime = svc.AllFacts.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

        string[] wired = [
            "audio.latency",                            // 平台組（App 啟動路徑）的對照組
            "boot.duration_ms", "boot.last_time",       // 2.43 接線
            "net.offload",                              // 2.43 接線
            "esp.partitions.count", "esp.files", "esp.dbx", // 2.43 ESP
            "storage.reliability.count",                // 2.38 儲存可靠性
            "pmu.uncore.platform", "pmu.uncore.ratio_limit", "pmu.uncore.perf_status", // 2.47 Uncore
        ];
        var missing = wired.Where(k => !runtime.Contains(k)).ToList();
        Assert.True(missing.Count == 0,
            "這些鍵的服務已接進 LoadUsermodeFacts／ReloadInto，本次執行期卻沒生產——" +
            "接線掉了或服務改成不可得時回空（違反『讀不到要標三態不是不產』）：" + string.Join("、", missing));
    }

    [Fact]
    public void 條件白名單有上限且理由講得清楚()
    {
        Assert.True(ConditionalOnly.Count <= ConditionalCap,
            $"條件鍵白名單已達 {ConditionalOnly.Count}（上限 {ConditionalCap}）——太多鍵只在互動裡活著，申報的可信度要重新審視");
        foreach (var (key, reason) in ConditionalOnly)
            Assert.True(reason.Trim().Length >= 20, $"{key}：條件鍵的理由不夠清楚");
    }
}
