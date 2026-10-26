using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 第 274 輪：<c>ata.smart.attr.lookup</c> 的真值接線。
///
/// <para>
/// <b>這一組一條真碟都不讀。</b>碟清單、匯流排型別、屬性列全部注入——
/// 理由不是怕慢，是<b>本機的 NVMe 讀取會卡在核心不可中斷等待</b>（第 263 輪實證：
/// 連 <c>taskkill /F</c> 都殺不掉）。會在測試裡真的去問碟的測試，
/// 失敗時不是紅燈，是<b>整個測試行程停住</b>。
/// </para>
/// <para>
/// 鍵一律用假索引（8675321 起跳，與 <see cref="StorageProbeBreakerTests"/> 的假索引區隔開），
/// 避免與其他測試類別的斷路器狀態互相影響。
/// </para>
/// </summary>
public class SmartLookupProbeTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    // 假碟索引：不存在的編號，任何真實枚舉都不會指到它們。
    private const int Ata = 8675321;
    private const int Nvme = 8675322;
    private const int Usb = 8675323;
    private const int AtaTimeout = 8675324;
    private const int AtaStats = 8675325;
    private const int AtaModel = 8675326;
    private const int AtaEmpty = 8675327;

    private const uint BusAta = 11;      // BusTypeSata
    private const uint BusNvme = 17;     // BusTypeNvme
    private const uint BusUsb = 7;       // BusTypeUsb

    private static PeripheralFirmwareFactsService.PeripheralInventory Inv(
        params (int Index, string Model)[] disks)
        => new([.. disks.Select(d => new PeripheralFirmwareFactsService.DeviceFirmware("硬碟", d.Model, "1.0", d.Index))],
            PhysicalNics: null);

    private static SmartRow Row(string name, byte id) => new(name, "100", "90", "", id, null);

    // ── 通道篩選：只讀 ATA ───────────────────────────────────────────────

    [Fact]
    public void 只讀ATA通道的碟_NVMe與其他通道逐顆記下原因()
    {
        var read = new List<int>();
        var bus = new Dictionary<int, uint> { [Ata] = BusAta, [Nvme] = BusNvme, [Usb] = BusUsb };

        var result = SmartLookupProbe.Run(
            inventory: () => Inv((Ata, "Samsung SSD 870 EVO"), (Nvme, "Samsung SSD 990 PRO"), (Usb, "USB 隨身碟")),
            busOf: i => bus[i],
            readAttributes: (i, _) => { read.Add(i); return [Row("5 重配置磁區數", 5)]; });

        Assert.Equal(new[] { Ata }, read);                       // NVMe／USB 一次都沒被讀
        Assert.Equal(SmartLookupProbe.ProbeStatus.Ok, result.Status);
        Assert.Single(result.Rows);
        Assert.Equal(new[] { Ata }, result.ReadIndexes);
        Assert.Equal(2, result.Skipped.Count);
    }

    [Fact]
    public void NVMe略過的原因成文_指名卡死而不是不明原因跳過()
    {
        var result = SmartLookupProbe.Run(
            inventory: () => Inv((Ata, "Samsung SSD 870 EVO"), (Nvme, "Samsung SSD 990 PRO")),
            busOf: i => i == Ata ? BusAta : BusNvme,
            readAttributes: (_, _) => [Row("5 重配置磁區數", 5)]);

        var nvmeSkip = Assert.Single(result.Skipped, s => s.Index == Nvme);
        Assert.Contains("本專案不在收集時讀 NVMe 日誌頁——實測會卡死", nvmeSkip.Reason);
        Assert.Contains("第 263 輪", nvmeSkip.Reason);
        Assert.Contains("taskkill", nvmeSkip.Reason);
        // 「為什麼沒讀」必須一路傳到事實的值裡，不然讀者只看得到統計數字。
        Assert.Contains("略過 1 顆", result.CoverageNote);
        Assert.Contains("NVMe 日誌頁", result.CoverageNote);
    }

    [Fact]
    public void 非ATA非NVMe通道_說出它沒有直讀通道且不猜()
    {
        var result = SmartLookupProbe.Run(
            inventory: () => Inv((Usb, "USB 隨身碟")),
            busOf: _ => BusUsb,
            readAttributes: (_, _) => throw new InvalidOperationException("不該被呼叫"));

        var skip = Assert.Single(result.Skipped);
        Assert.Contains("USB", skip.Reason);
        Assert.Contains("沒有可用的唯讀直讀通道", skip.Reason);
        Assert.Equal(SmartLookupProbe.ProbeStatus.NotApplicable, result.Status);
    }

    [Fact]
    public void 沒有Index的碟_記成無法定位而不是略過無聲()
    {
        var result = SmartLookupProbe.Run(
            inventory: () => Inv((-1, "不明裝置")),
            busOf: _ => throw new InvalidOperationException("沒有 Index 不該去問匯流排"),
            readAttributes: (_, _) => throw new InvalidOperationException("沒有 Index 不該去讀"));

        var skip = Assert.Single(result.Skipped);
        Assert.Equal(-1, skip.Index);
        Assert.Contains("沒有給 Index", skip.Reason);
    }

    // ── 型號要進解碼器（知識表補強路徑的前提）─────────────────────────────

    [Fact]
    public void 碟型號會帶進讀取_否則廠商自訂永遠只會是未收錄()
    {
        string? seenModel = null;
        int? seenIndex = null;

        _ = SmartLookupProbe.Run(
            inventory: () => Inv((AtaModel, "Samsung SSD 870 EVO")),
            busOf: _ => BusAta,
            readAttributes: (i, model) => { seenIndex = i; seenModel = model; return [Row("5 重配置磁區數", 5)]; });

        Assert.Equal(AtaModel, seenIndex);
        Assert.Equal("Samsung SSD 870 EVO", seenModel);
    }

    [Fact]
    public void 真的解碼器_帶型號才會把廠商自訂補成命中()
    {
        // 這不是磁碟讀取：只是一張 512 位元組的假 SMART 表（ID 177 一筆）。
        var sector = new byte[512];
        sector[0] = 0x10; sector[1] = 0x80;
        int o = 2;
        sector[o] = 177; sector[o + 3] = 100; sector[o + 4] = 99; sector[o + 5] = 0x11;

        var withModel = StorageSmartService.DecodeAtaAttributes(sector, "Samsung SSD 870 EVO");
        var withoutModel = StorageSmartService.DecodeAtaAttributes(sector);

        Assert.StartsWith("177 廠商自訂（", withModel[0].Name);   // 知識表命中
        Assert.Equal("177 廠商自訂", withoutModel[0].Name);       // 沒有型號＝未收錄
        Assert.Equal(1, KnowledgeLookupCollector.CollectSmart(withModel).Hits);
        Assert.Equal(0, KnowledgeLookupCollector.CollectSmart(withoutModel).Hits);
    }

    // ── 讀不到的三態 ───────────────────────────────────────────────────

    [Fact]
    public void 逾時如實標成讀錯誤_不阻塞也不編數字()
    {
        var result = SmartLookupProbe.Run(
            inventory: () => Inv((AtaTimeout, "Samsung SSD 870 EVO")),
            busOf: _ => BusAta,
            readAttributes: (_, _) => { Thread.Sleep(2000); return [Row("5 重配置磁區數", 5)]; },
            budgetMs: 80);

        Assert.Equal(SmartLookupProbe.ProbeStatus.ReadError, result.Status);
        Assert.Contains("逾時", result.Reason);
        Assert.Empty(result.Rows);
        Assert.Empty(result.ReadIndexes);

        var fact = KnowledgeLookupCollector.ToSmartFact(At,
            KnowledgeLookupCollector.CollectSmart(result.Rows), result);
        Assert.Equal("ata.smart.attr.lookup", fact.Key);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Null(fact.NumericValue);   // 非 Present 不得帶數字
        Assert.Contains("逾時", fact.UnavailableReason);
    }

    [Fact]
    public void 讀到了但零屬性_是Unknown不是讀不到()
    {
        var result = SmartLookupProbe.Run(
            inventory: () => Inv((AtaEmpty, "Samsung SSD 870 EVO")),
            busOf: _ => BusAta,
            readAttributes: (_, _) => []);

        Assert.Equal(SmartLookupProbe.ProbeStatus.Unknown, result.Status);
        Assert.Equal(new[] { AtaEmpty }, result.ReadIndexes);
        Assert.Empty(result.Rows);

        var fact = KnowledgeLookupCollector.ToSmartFact(At,
            KnowledgeLookupCollector.CollectSmart(result.Rows), result);
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Null(fact.NumericValue);
        Assert.Contains("屬性表是空的", fact.UnavailableReason);
    }

    [Fact]
    public void 沒有ATA碟_是NotApplicable並說出沒有對象()
    {
        var result = SmartLookupProbe.Run(
            inventory: () => Inv((Nvme, "Samsung SSD 990 PRO")),
            busOf: _ => BusNvme,
            readAttributes: (_, _) => throw new InvalidOperationException("沒有任何 ATA 碟可讀"));

        Assert.Equal(SmartLookupProbe.ProbeStatus.NotApplicable, result.Status);
        Assert.Empty(result.Rows);
        Assert.Equal(1, result.Skipped.Count);

        var fact = KnowledgeLookupCollector.ToSmartFact(At,
            KnowledgeLookupCollector.CollectSmart(result.Rows), result);
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Null(fact.NumericValue);
        Assert.Contains("NVMe 日誌頁", fact.UnavailableReason);   // 略過的原因仍看得見
    }

    [Fact]
    public void 碟列舉失敗_與沒有碟分開講()
    {
        var nul = SmartLookupProbe.Run(
            inventory: () => null,
            busOf: _ => BusAta,
            readAttributes: (_, _) => []);
        Assert.Equal(SmartLookupProbe.ProbeStatus.ReadError, nul.Status);
        Assert.Contains("列舉失敗", nul.Reason);

        var threw = SmartLookupProbe.Run(
            inventory: () => throw new InvalidOperationException("WMI 不在"),
            busOf: _ => BusAta,
            readAttributes: (_, _) => []);
        Assert.Equal(SmartLookupProbe.ProbeStatus.ReadError, threw.Status);
        Assert.Contains("WMI 不在", threw.Reason);
        Assert.Equal(FactAvailability.ReadError,
            KnowledgeLookupCollector.ToSmartFact(At, KnowledgeLookupReport.LookupStats.Empty, threw).Availability);
    }

    [Fact]
    public void 明顯是零顆碟_是NotApplicable不是讀不到()
    {
        var result = SmartLookupProbe.Run(
            inventory: () => Inv(),
            busOf: _ => BusAta,
            readAttributes: (_, _) => []);

        Assert.Equal(SmartLookupProbe.ProbeStatus.NotApplicable, result.Status);
        Assert.Contains("沒有列舉到實體硬碟", result.Reason);
    }

    // ── 統計數字：命中／未收錄必須與注入的列數一致 ────────────────────────

    [Fact]
    public void 有屬性時統計數字與注入的廠商自訂列數一致()
    {
        var result = SmartLookupProbe.Run(
            inventory: () => Inv((AtaStats, "Samsung SSD 870 EVO")),
            busOf: _ => BusAta,
            readAttributes: (_, _) =>
            [
                Row("177 廠商自訂（Wear Leveling Count（耗損平衡計數））", 177),   // 命中
                Row("199 廠商自訂", 199),                                          // 未收錄
                Row("5 重配置磁區數", 5),                                          // 既有對照表——不經知識表
            ]);

        var stats = KnowledgeLookupCollector.CollectSmart(result.Rows);
        Assert.Equal(2, stats.Total);        // 只有「廠商自訂」那兩列走過知識表
        Assert.Equal(1, stats.Hits);
        Assert.Equal(1, stats.NotListed);

        var fact = KnowledgeLookupCollector.ToSmartFact(At, stats, result);
        Assert.Equal(KnowledgeLookupCollector.SmartAttrLookupKey, fact.Key);
        Assert.Equal(FactAvailability.Unknown, fact.Availability);   // 有未收錄＝缺口，不是錯誤
        Assert.Null(fact.NumericValue);
        Assert.Contains("已讀 ATA 1 顆（屬性 3 筆）", fact.Value);
    }

    [Fact]
    public void 全部命中時是Present且數字等於命中數()
    {
        var result = SmartLookupProbe.Run(
            inventory: () => Inv((AtaStats, "Samsung SSD 870 EVO")),
            busOf: _ => BusAta,
            readAttributes: (_, _) => [Row("177 廠商自訂（Wear Leveling Count（耗損平衡計數））", 177)]);

        var fact = KnowledgeLookupCollector.ToSmartFact(At,
            KnowledgeLookupCollector.CollectSmart(result.Rows), result);

        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Equal(1, fact.NumericValue);
        Assert.Null(fact.UnavailableReason);   // Present 不得帶「為什麼讀不到」
        Assert.Contains("已讀 ATA 1 顆", fact.Value);
    }

    // ── 沒讀的那一輪：鍵仍在、如實說沒讀 ──────────────────────────────────

    [Fact]
    public void 未載入時的鍵仍在_並說出沒有讀而不是沒有對象()
    {
        var fact = KnowledgeLookupCollector.ToSmartNotLoaded(At);

        Assert.Equal("ata.smart.attr.lookup", fact.Key);
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Null(fact.NumericValue);
        Assert.Contains("尚未讀取", fact.Value);
        Assert.Contains("不是「沒有廠商自訂屬性」", fact.UnavailableReason);
    }
}
