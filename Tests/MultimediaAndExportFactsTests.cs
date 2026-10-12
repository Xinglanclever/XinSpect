using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Vol2 推進第 99–103 項（多媒體＋匯出／快取）的守門：全部注入假件。
/// 紅線：音訊只數端點不錄音；編解碼器只讀登錄清單；JSONL 敏感值預設遮蔽；快取殘留＝Unknown。
/// </summary>
public class MultimediaAndExportFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 14, 0, 0, TimeSpan.Zero);

    private static HardwareFact F(IReadOnlyList<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    private static IReadOnlyList<HardwareFact> Mm(
        MultimediaFactsService.RegistryMapProbe? drivers32 = null,
        MultimediaFactsService.EndpointProbe? endpoints = null,
        MultimediaFactsService.FileVersionProbe? version = null,
        MultimediaFactsService.KeyExistsProbe? keyExists = null)
        => MultimediaFactsService.Collect(At, drivers32, endpoints, version, keyExists);

    // ── MU-001 編解碼器 ─────────────────────────────────────────────────

    [Fact]
    public void 編解碼器_清單數與前綴分組()
    {
        var facts = Mm(drivers32: _ => new Dictionary<string, string>
        {
            ["msacm.l3acm"] = "l3codeca.acm",
            ["vidc.mrle"] = "msrle32.dll",
            ["vidc.msvc"] = "msvidc32.dll",
            ["wave"] = "wdmaud.drv",
        });
        var count = F(facts, MultimediaFactsService.CodecCountKey);
        var summary = F(facts, MultimediaFactsService.CodecSummaryKey);

        Assert.Equal(4, count.NumericValue);
        Assert.Contains("VIDC×2", summary.Value);
        Assert.Contains("不等於「所有可用編解碼器」", summary.Value);
    }

    [Fact]
    public void 編解碼器_讀取失敗_ReadError()
    {
        Assert.Equal(FactAvailability.ReadError, F(Mm(drivers32: _ => null), MultimediaFactsService.CodecCountKey).Availability);
    }

    [Fact]
    public void 編解碼器_空登錄_0是值()
    {
        var facts = Mm(drivers32: _ => new Dictionary<string, string>());
        Assert.Equal(0, F(facts, MultimediaFactsService.CodecCountKey).NumericValue);
    }

    // ── MU-005 端點（不錄音紅線）────────────────────────────────────────

    [Fact]
    public void 端點_只數數量_紅線寫進值裡()
    {
        var facts = Mm(endpoints: () => (5, 3));
        var summary = F(facts, MultimediaFactsService.MicSummaryKey);

        Assert.Equal(5, F(facts, MultimediaFactsService.MicCountKey).NumericValue);
        Assert.Contains("使用中 3 個", summary.Value);
        Assert.Contains("不錄音", summary.Value);
        Assert.Contains("不等於有麥克風陣列硬體", summary.Value);
    }

    [Fact]
    public void 端點_列舉失敗_ReadError()
    {
        Assert.Equal(FactAvailability.ReadError, F(Mm(endpoints: () => null), MultimediaFactsService.MicCountKey).Availability);
    }

    [Fact]
    public void 端點_零個_0是值()
    {
        var facts = Mm(endpoints: () => (0, 0));
        Assert.Equal(0, F(facts, MultimediaFactsService.MicCountKey).NumericValue);
    }

    // ── MU-007 Media Foundation ─────────────────────────────────────────

    [Fact]
    public void MF_平台鍵與版本如實()
    {
        var f = F(Mm(version: _ => "10.0.26100.1", keyExists: _ => true), MultimediaFactsService.MfKey);
        Assert.Contains("存在", f.Value);
        Assert.Contains("10.0.26100.1", f.Value);
        Assert.Contains("不測播放", f.Value);
    }

    [Fact]
    public void MF_鍵不存在_如實說沒有()
    {
        var f = F(Mm(version: _ => "10.0.26100.1", keyExists: _ => false), MultimediaFactsService.MfKey);
        Assert.Contains("不存在", f.Value);
    }

    // ── RS-006 JSONL ────────────────────────────────────────────────────

    private static HardwareFact Fact(string key, string value, bool sensitive = false) =>
        new(key, "測試域", key, value, "", "測試", FactTrustLevel.Reported, sensitive, At);

    [Fact]
    public async Task JSONL_每行一物件_敏感值預設遮蔽()
    {
        var facts = new List<HardwareFact>
        {
            Fact("cpu.name", "Test CPU"),
            Fact("board.serial", "SECRET-123", sensitive: true),
            new("bad.f", "", "", "", "", "s", FactTrustLevel.Unknown, false, At, null,
                FactAvailability.ReadError, "讀不到"),
        };
        string path = Path.Combine(Path.GetTempPath(), "xinjsonl-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            await JsonlExportService.WriteAsync(path, facts);

            var lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length);

            var docs = lines.Select(l => JsonDocument.Parse(l)).ToList();
            using var serialDoc = docs.Single(d => d.RootElement.GetProperty("key").GetString() == "board.serial");
            Assert.Equal("（敏感值已遮蔽）", serialDoc.RootElement.GetProperty("value").GetString());

            using var badDoc = docs.Single(d => d.RootElement.GetProperty("key").GetString() == "bad.f");
            Assert.Equal("ReadError", badDoc.RootElement.GetProperty("availability").GetString());
            Assert.Equal("讀不到", badDoc.RootElement.GetProperty("unavailableReason").GetString());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task JSONL_Preserve政策_保留敏感值()
    {
        var facts = new List<HardwareFact> { Fact("board.serial", "SECRET-123", sensitive: true) };
        string path = Path.Combine(Path.GetTempPath(), "xinjsonl-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            await JsonlExportService.WriteAsync(path, facts, SensitiveValuePolicy.Preserve);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("SECRET-123", doc.RootElement.GetProperty("value").GetString());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void JSONL_描述鍵_講清是資料流不是報告()
    {
        var f = JsonlExportService.Describe(At);
        Assert.Contains("不是報告", f.Value);
        Assert.Contains("零網路", f.Value);
    }

    // ── QS-007 查詢快取 ─────────────────────────────────────────────────

    [Fact]
    public void 快取_同指紋命中_不同指紋重算()
    {
        var cache = new QueryResultCache();
        int builds = 0;

        var (r1, cached1) = cache.GetOrBuild("cpu.*", "fp1", () => { builds++; return [Fact("cpu.a", "1")]; });
        var (r2, cached2) = cache.GetOrBuild("cpu.*", "fp1", () => { builds++; return [Fact("cpu.a", "1")]; });
        var (r3, cached3) = cache.GetOrBuild("cpu.*", "fp2", () => { builds++; return [Fact("cpu.a", "2")]; });

        Assert.False(cached1);
        Assert.True(cached2);
        Assert.False(cached3);
        Assert.Equal(2, builds);   // 第一次 miss＋指紋換了再 miss
        Assert.Equal(r1.Count, r2.Count);   // 命中回的是同一份清單（引用相等性不適用 List）
        Assert.Equal(1, cache.Hits);   // 第二次同指紋命中一次
        Assert.Equal(2, cache.Misses);   // 第一次＋指紋換掉
    }

    [Fact]
    public void 快取_失效_全部重算()
    {
        var cache = new QueryResultCache();
        cache.GetOrBuild("a", "fp", () => [Fact("a", "1")]);
        cache.Invalidate();
        Assert.Equal(0, cache.Entries);          // 失效後必須真的清空
        cache.GetOrBuild("a", "fp", () => [Fact("a", "1")]);
        Assert.Equal(1, cache.Entries);
        Assert.Equal(1, cache.Invalidations);
        Assert.Equal(2, cache.Misses);
    }

    [Fact]
    public void 快取_空查詢_拋例外()
    {
        Assert.Throws<ArgumentException>(() => new QueryResultCache().GetOrBuild("", "fp", () => []));
    }

    [Fact]
    public void 快取_有事實_值講清界線()
    {
        var cache = new QueryResultCache();
        cache.GetOrBuild("a", "fp", () => [Fact("a", "1")]);
        var f = cache.ToFact(At);

        Assert.Contains("命中 1", f.Value);
        Assert.Contains("不做時間型快取", f.Value);
        Assert.Equal(FactAvailability.Present, f.Availability);
    }

    [Fact]
    public void 快取_沒有查詢_標Unknown不是0_效能()
    {
        var f = new QueryResultCache().ToFact(At);
        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("不是 0% 效能", f.UnavailableReason);
    }
}
