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
        EvidenceCollection.ReloadInto(svc);          // 驅動後端組
        EvidenceCollection.LoadUsermodeFacts(svc);   // usermode 組
        svc.LoadPlatformFacts();                     // 平台全組（App 啟動序列的第三條入口）
        return svc;
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
