using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// PCI 名稱的<b>出處分層</b>（第 284 輪）。
///
/// <para>
/// 第 281 輪把 pci.ids 快照接進來之後，「這個名字是誰說的」有兩種答案：
/// 本專案核對過的 15 家，或社群維護的兩萬多筆登錄檔。<c>pci.id</c> 當時仍用
/// 「值裡有沒有出現『未收錄』」來數命中，於是<b>兩層在統計上完全一樣</b>——
/// 一張卡的裝置名是從登錄檔撈的，跟手工表查到的廠商名一起算成「命中」。
/// </para>
///
/// <para>
/// 這一組守三件事：①值裡一定要有機器可讀的出處段落（不是靠相鄰的中文猜）；
/// ②回收端照那一段分類，登錄檔命中不會被算成手工表；③<c>pci.id</c> 的文字要把
/// 兩種知識的擔保差異說出來。
/// </para>
/// </summary>
public class PciNameProvenanceTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact DevFact(string value) =>
        new("pci.dev.1f.2", "匯流排", "PCI 0:1F.2", value, "",
            "PCI 設定空間 0x00/0x08", FactTrustLevel.Measured, false, At);

    private static (byte, byte, byte, byte) Cls() => (0x02, 0x00, 0x00, 0x03);

    // ── 產生端：值裡必須有出處段落 ──────────────────────────────────────

    [Fact]
    public void 描述一定帶名稱出處段落_兩段都在()
    {
        string text = PciIdLookup.Describe((0x8086, 0x15BE), Cls());

        Assert.Contains(PciIdLookup.OriginClausePrefix, text);
        Assert.Contains("廠商=", text);
        Assert.Contains("裝置=", text);
        // 手工表認得 Intel，裝置名只可能來自登錄檔。
        Assert.Contains($"廠商={PciIdLookup.CuratedToken}", text);
        Assert.Contains($"裝置={PciIdLookup.RegistryToken}", text);
        Assert.Contains(PciIdDatabase.SnapshotVersion, text);   // 登錄檔層要附快照
    }

    [Theory]
    [InlineData(0x8086, 0x15BE)]   // 廠商手工表、裝置登錄檔
    [InlineData(0x1234, 0x5678)]   // 兩層都沒有
    [InlineData(0xFFFF, 0xFFFF)]   // 廠商是登錄檔的哨兵值
    public void 裝置段永遠不會標成手工表(ushort vendor, ushort device)
    {
        // 手工表從來沒有收錄裝置型號（第 281 輪的設計）。值裡若出現「裝置=手工表」，
        // 就是有人把廠商表的答案當成裝置名——那會讓一個 15 筆的表替兩萬筆登錄檔背書。
        string clause = PciIdLookup.Describe((vendor, device), Cls())
            .Split(PciIdLookup.OriginClausePrefix)[1];

        string devicePart = clause.Split('、').Single(s => s.StartsWith("裝置=", StringComparison.Ordinal));
        Assert.DoesNotContain(PciIdLookup.CuratedToken, devicePart);
    }

    [Fact]
    public void 出處代號是穩定字串_不是靠中文句子推斷()
    {
        // 產生端與回收端共用這三個常數；改字會兩邊一起改，而不是讓統計安靜地改口。
        Assert.Equal("手工表", PciIdLookup.CuratedToken);
        Assert.Equal("登錄檔", PciIdLookup.RegistryToken);
        Assert.Equal("未收錄", PciIdLookup.UnlistedToken);
        Assert.Equal("名稱出處：", PciIdLookup.OriginClausePrefix);
    }

    // ── 回收端：照出處段落分類 ──────────────────────────────────────────

    [Fact]
    public void 回收端照出處分段分類_登錄檔不會被算成手工表()
    {
        var facts = new[] { DevFact(PciIdLookup.Describe((0x8086, 0x15BE), Cls())) };

        var (devices, names) = KnowledgeLookupCollector.CollectPci(facts);

        Assert.Equal(2, names.Total);          // 廠商＋裝置兩段
        Assert.Equal(1, names.Curated);
        Assert.Equal(1, names.Registry);
        Assert.Equal(0, names.Unlisted);
        Assert.Equal(0, names.Unparsable);

        Assert.Equal(1, devices.Total);
        Assert.Equal(1, devices.Hits);         // 兩段都說得出名字＝這張卡命中
    }

    [Fact]
    public void 廠商認得但裝置未收錄_裝置級算查不到_名稱級只算一段未收錄()
    {
        var facts = new[] { DevFact(PciIdLookup.Describe((0x8086, 0x5678), Cls())) };

        var (devices, names) = KnowledgeLookupCollector.CollectPci(facts);

        Assert.Equal(1, names.Curated);
        Assert.Equal(1, names.Unlisted);
        Assert.Equal(1, devices.NotListed);    // 這張卡有東西沒對到，kp.miss 要看得到
    }

    [Fact]
    public void 沒有出處段落的值算無法分類而不是命中()
    {
        // 舊碼會把任何不含「未收錄」的值算成命中——包括這種什麼都沒說的。
        var facts = new[]
        {
            DevFact("Vendor 0x8086:0x15BE（類別碼讀取失敗，只報原始 ID）"),
            DevFact(""),
        };

        var (devices, names) = KnowledgeLookupCollector.CollectPci(facts);

        Assert.Equal(2, names.Unparsable);
        Assert.Equal(0, names.Curated + names.Registry + names.Unlisted);
        Assert.Equal(2, devices.Unparsable);
        Assert.Equal(0, devices.Hits);
    }

    [Fact]
    public void 有出處段落但讀不懂_算無法分類而不是未收錄()
    {
        // 「表沒收錄這一筆」與「我們的分類程式看不懂自己寫的字」是兩件事：
        // 前者要補表，後者要修程式。壓成同一個數字，就會去補一張沒有問題的表。
        var facts = new[]
        {
            DevFact($"某卡 ・ {PciIdLookup.OriginClausePrefix}廠商=?、裝置=?"),
            DevFact($"某卡 ・ {PciIdLookup.OriginClausePrefix}廠商={PciIdLookup.CuratedToken}、裝置=???"),
        };

        var (devices, names) = KnowledgeLookupCollector.CollectPci(facts);

        Assert.Equal(2, names.Unparsable);
        Assert.Equal(1, names.Curated);       // 第二張卡的廠商段仍然算得出來
        Assert.Equal(0, names.Unlisted);
        Assert.Equal(2, devices.Unparsable);  // 兩張卡都有讀不懂的段落
        Assert.Equal(0, devices.Hits);
    }

    [Fact]
    public void 回收端不重新查表_只讀回值裡已經寫下的結論()
    {
        // 兩張卡共用一個值 → 統計就是兩份，不會因為「實際查表只有一個答案」而被合併。
        // 這條釘住「回收」的語意：它數的是畫面上的結論，不是查表次數。
        string value = PciIdLookup.Describe((0x8086, 0x15BE), Cls());
        var (devices, names) = KnowledgeLookupCollector.CollectPci([DevFact(value), DevFact(value)]);

        Assert.Equal(2, devices.Total);
        Assert.Equal(4, names.Total);
    }

    // ── 事實輸出 ────────────────────────────────────────────────────────

    [Fact]
    public void pci_id要同時說出三種計數與名稱段的定義()
    {
        var fact = KnowledgeLookupReport.ToPciFact(At,
            new KnowledgeLookupReport.PciNameStats(Curated: 2, Registry: 6, Unlisted: 0, Unparsable: 0));

        Assert.Contains("8 個名稱段", fact.Value);
        Assert.Contains("每張卡＝廠商＋裝置兩段", fact.Value);   // 單位要寫出來
        Assert.Contains($"{PciIdLookup.CuratedToken} 2", fact.Value);
        Assert.Contains($"{PciIdLookup.RegistryToken} 6", fact.Value);
        Assert.Contains($"{PciIdLookup.UnlistedToken} 0", fact.Value);

        // 界線：兩種知識的擔保不同，不能合成一個「命中」。
        Assert.Contains("錯誤率不是零", fact.Value);
        Assert.Contains("不讓後者借前者的信用", fact.Value);

        Assert.Equal(8, fact.NumericValue);   // 說得出名字的段數
    }

    [Fact]
    public void pci_id三種狀態分開_全部有名才Present()
    {
        var present = KnowledgeLookupReport.ToPciFact(At,
            new KnowledgeLookupReport.PciNameStats(1, 3, 0, 0));
        Assert.Equal(FactAvailability.Present, present.Availability);
        Assert.Null(present.UnavailableReason);

        var unknown = KnowledgeLookupReport.ToPciFact(At,
            new KnowledgeLookupReport.PciNameStats(1, 3, 1, 0));
        Assert.Equal(FactAvailability.Unknown, unknown.Availability);
        Assert.Contains("要補的是知識表", unknown.UnavailableReason);
        Assert.Null(unknown.NumericValue);   // 非 Present 不得帶數字（不變式）

        var readError = KnowledgeLookupReport.ToPciFact(At,
            new KnowledgeLookupReport.PciNameStats(1, 3, 0, 2));
        Assert.Equal(FactAvailability.ReadError, readError.Availability);
        Assert.Contains("不是知識表的缺口", readError.UnavailableReason);
    }

    [Fact]
    public void pci_id沒有名稱段時標不適用而不是回零()
    {
        var fact = KnowledgeLookupReport.ToPciFact(At, KnowledgeLookupReport.PciNameStats.Empty);

        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Null(fact.NumericValue);       // 「無從回答」不得帶 0
        Assert.Contains("沒有查詢", fact.Value);
    }

    [Fact]
    public void 真機的每一張卡都帶得出可分類的出處段落()
    {
        // 這一條跑真的 PciKnowledge 查表與真的值格式，確保兩端接得起來
        // （不是靠測試自己造一個「理想值」）。
        var facts = new List<HardwareFact>();
        for (byte dev = 0x00; dev <= 0x1F; dev++)
        {
            var ids = ((ushort)(0x8086 + dev), (ushort)(0x15B0 + dev));
            facts.Add(new HardwareFact($"pci.dev.{dev:x2}.0", "匯流排", $"PCI 0:{dev:X2}.0",
                PciIdLookup.Describe(ids, Cls()), "", "PCI 設定空間 0x00/0x08",
                FactTrustLevel.Measured, false, At));
        }

        var (devices, names) = KnowledgeLookupCollector.CollectPci(facts);

        Assert.Equal(0, devices.Unparsable);
        Assert.Equal(0, names.Unparsable);
        Assert.Equal(facts.Count * 2, names.Total);
    }
}
