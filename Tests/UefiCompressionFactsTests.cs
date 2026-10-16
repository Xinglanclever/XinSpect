using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 映像壓縮型別支援清單的守門（ULTIMATE-1000 階段二 P2-038）。
///
/// <para>
/// <b>核心是「認得」與「能解壓」之別。</b>本版認得 LZMA 與 CRC32 的 GUID
/// 並標出名字，但**不解壓**——混淆這兩者會讓使用者以為「支援」＝「能還原內容」。
/// </para>
/// </summary>
public class UefiCompressionFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 認得的引擎清單要含LZMA與CRC32()
    {
        var f = UefiCompressionFactsService.Collect(At)
            .Single(x => x.Key == UefiCompressionFactsService.RecognizedKey);

        Assert.Contains("LZMA 壓縮", f.Value);
        Assert.Contains("CRC32 引導", f.Value);
        Assert.Equal(2.0, f.NumericValue);
    }

    [Fact]
    public void 清單要說出認得不等於能解壓()
    {
        var f = UefiCompressionFactsService.Collect(At)
            .Single(x => x.Key == UefiCompressionFactsService.RecognizedKey);

        Assert.Contains("「認得」不等於「能解壓」", f.UnavailableReason);
        Assert.Contains("不解壓", f.UnavailableReason);
        Assert.Contains("不是「本版能還原內容的引擎」", f.UnavailableReason);
    }

    [Fact]
    public void 清單要標明每一個都是只標名不解壓()
    {
        var f = UefiCompressionFactsService.Collect(At)
            .Single(x => x.Key == UefiCompressionFactsService.RecognizedKey);

        Assert.Contains("只標名不解壓", f.Value);
        Assert.DoesNotContain("可解）", f.Value.Replace("可解）", "可解）").Replace("，可解）", "X"));
    }

    /// <summary>
    /// <b>單一來源的守門。</b>GUID 必須直接取自 <see cref="UefiFv"/> 的常數——
    /// 兩份 GUID 表就是兩處漂移的來源。
    /// </summary>
    [Fact]
    public void GUID必須來自既有的解碼器常數()
    {
        var engines = UefiCompressionFactsService.Recognized();

        Assert.Contains(engines, e =>
            e.Guid == UefiFv.GuidLzmaCustomDecompress.ToString().ToUpperInvariant());
        Assert.Contains(engines, e =>
            e.Guid == UefiFv.GuidCrc32GuidedSection.ToString().ToUpperInvariant());
    }

    [Fact]
    public void 沒有掃過映像時要說出它不自己去讀BIOS區()
    {
        var f = UefiCompressionFactsService.Collect(At)
            .Single(x => x.Key == UefiCompressionFactsService.DecompressedKey);

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不自己去讀 BIOS 區", f.UnavailableReason);
        Assert.Contains("有它自己的可及性事實", f.UnavailableReason);
    }

    [Fact]
    public void 沒有壓縮區段要說那與掃描失敗不同()
    {
        var f = UefiCompressionFactsService.Collect(At, [])
            .Single(x => x.Key == UefiCompressionFactsService.DecompressedKey);

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("「沒有壓縮區段」與「掃描失敗」是不同的斷言", f.UnavailableReason);
    }

    [Fact]
    public void 遇到認得的引擎要標Present()
    {
        var guid = UefiFv.GuidLzmaCustomDecompress.ToString().ToUpperInvariant();
        var f = UefiCompressionFactsService.Collect(At, [guid])
            .Single(x => x.Key == UefiCompressionFactsService.DecompressedKey);

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("仍然不代表能解壓", f.UnavailableReason);
    }

    [Fact]
    public void 遇到未收錄的引擎要標Unknown並說那不代表有問題()
    {
        var f = UefiCompressionFactsService.Collect(At,
            ["DEADBEEF-0000-0000-0000-000000000000"])
            .Single(x => x.Key == UefiCompressionFactsService.DecompressedKey);

        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("1 個未收錄", f.Value);
        Assert.Contains("不代表它們有問題", f.UnavailableReason);
        Assert.Contains("未收錄引擎 <GUID>", f.UnavailableReason);
        Assert.Contains("而不是猜一個名字", f.UnavailableReason);
    }

    [Fact]
    public void 混合情況要同時報出數量與未收錄數()
    {
        var known = UefiFv.GuidLzmaCustomDecompress.ToString().ToUpperInvariant();
        var f = UefiCompressionFactsService.Collect(At, [known, "DEADBEEF-0000-0000-0000-000000000000"])
            .Single(x => x.Key == UefiCompressionFactsService.DecompressedKey);

        Assert.Equal(2.0, f.NumericValue);
        Assert.Contains("其中 1 個未收錄", f.Value);
    }

    [Fact]
    public void 失敗路徑與成功路徑的鍵集合必須相同()
    {
        var ok = UefiCompressionFactsService.Collect(At, ["X"])
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var none = UefiCompressionFactsService.Collect(At)
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(ok, none);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = UefiCompressionFactsService.Collect(At, ["X"]);
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "壓縮支援事實產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
