using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 量測開機支援旗標的守門（ULTIMATE-1000 階段二 P2-056）。
///
/// <para>
/// <b>核心是「有 TPM」與「有量測」之別。</b>一台機器可以有 TPM
/// 而關鍵 PCR 全零——那代表韌體沒有把任何東西 extend 進去。
/// </para>
/// </summary>
public class MeasuredBootFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly string Zero256 = new('0', 64);

    private static MeasuredBootFactsService.PcrReadings R(
        string? p0 = null, string? p2 = null, string? p4 = null, string? p7 = null)
        => new(p0, p2, p4, p7);

    // ── 三態 ────────────────────────────────────────────────────────────

    [Fact]
    public void 沒有讀值時要標不支援並說出不自己問TPM()
    {
        var facts = MeasuredBootFactsService.Collect(At, null);
        var m = facts.Single(f => f.Key == MeasuredBootFactsService.MeasuredKey);

        Assert.Equal(FactAvailability.NotSupported, m.Availability);
        Assert.Contains("不自己去問 TPM", m.UnavailableReason);
        Assert.Contains("tpm.present", m.UnavailableReason);
    }

    [Fact]
    public void 全部讀不到時要說那與全為零不同()
    {
        var facts = MeasuredBootFactsService.Collect(At, R());
        var m = facts.Single(f => f.Key == MeasuredBootFactsService.MeasuredKey);

        Assert.Equal(FactAvailability.InsufficientPrivilege, m.Availability);
        Assert.Contains("「讀不到」與「全為零」是不同的斷言", m.UnavailableReason);
        Assert.Contains("量測鏈沒有開始", m.UnavailableReason);
    }

    // ── 有量測 ──────────────────────────────────────────────────────────

    [Fact]
    public void PCR0非零要標有量測證據()
    {
        var facts = MeasuredBootFactsService.Collect(At, R(p0: new string('a', 64)));
        var m = facts.Single(f => f.Key == MeasuredBootFactsService.MeasuredKey);

        Assert.Equal(FactAvailability.Present, m.Availability);
        Assert.Equal(1.0, m.NumericValue);
        Assert.Contains("有量測證據", m.Value);
        Assert.Contains("PCR 0", m.Value);
    }

    [Fact]
    public void 有量測時要說出有TPM不等於有量測()
    {
        var facts = MeasuredBootFactsService.Collect(At, R(p0: new string('a', 64)));
        var m = facts.Single(f => f.Key == MeasuredBootFactsService.MeasuredKey);

        // **Present 的事實不得帶理由欄**（HardwareFact 的不變式）：說明被併進了 Value，
        // 所以這一節的斷言讀 f.Value，不是 f.UnavailableReason。
        Assert.Contains("有 TPM 不等於有量測開機", m.Value);
        Assert.Contains("這裡看到的是後者", m.Value);
    }

    [Fact]
    public void 部分PCR為零時要說那不代表那些層級沒執行()
    {
        var facts = MeasuredBootFactsService.Collect(At,
            R(p0: new string('a', 64), p4: new string('b', 64), p7: Zero256));
        var m = facts.Single(f => f.Key == MeasuredBootFactsService.MeasuredKey);

        Assert.Contains("1 個仍為零", m.Value);
        Assert.Contains("不代表那些層級沒有執行", m.Value);
        Assert.Contains("PCR 7 只在 Secure Boot 啟用時才延伸", m.Value);
    }

    [Fact]
    public void 四個都延伸時要說出來()
    {
        var facts = MeasuredBootFactsService.Collect(At,
            R(new string('a', 64), new string('b', 64), new string('c', 64), new string('d', 64)));
        var m = facts.Single(f => f.Key == MeasuredBootFactsService.MeasuredKey);

        Assert.Contains("四個關鍵 PCR 都已延伸", m.Value);
    }

    [Fact]
    public void SHA1長度的全零也要認得()
    {
        var facts = MeasuredBootFactsService.Collect(At, R(p0: new string('0', 40)));
        var m = facts.Single(f => f.Key == MeasuredBootFactsService.MeasuredKey);

        Assert.Equal(FactAvailability.NotApplicable, m.Availability);
        // **非 Present 的事實不得帶數字**（HardwareFact 的不變式）：0 已被收斂成 null，
        // 而「原本是多少」留在理由欄的痕跡裡（不是靜默修正）。
        Assert.Null(m.NumericValue);
        Assert.Contains("原本帶了數字 0", m.UnavailableReason);
    }

    // ── 沒有量測 ────────────────────────────────────────────────────────

    [Fact]
    public void 全部為零要說出量測鏈沒開始()
    {
        var facts = MeasuredBootFactsService.Collect(At, R(Zero256, Zero256, Zero256, Zero256));
        var m = facts.Single(f => f.Key == MeasuredBootFactsService.MeasuredKey);

        Assert.Equal(FactAvailability.NotApplicable, m.Availability);
        // **非 Present 的事實不得帶數字**（HardwareFact 的不變式）：0 已被收斂成 null，
        // 而「原本是多少」留在理由欄的痕跡裡（不是靜默修正，見 HardwareFactInvariantTests）。
        Assert.Null(m.NumericValue);
        Assert.Contains("原本帶了數字 0", m.UnavailableReason);
        Assert.Contains("沒有量測證據", m.Value);
        Assert.Contains("4 個關鍵 PCR 都可讀，但全為零", m.Value);
        Assert.Contains("量測鏈根本沒開始", m.UnavailableReason);
    }

    [Fact]
    public void 全為零時要說出平台可以關閉量測而TPM仍在()
    {
        var facts = MeasuredBootFactsService.Collect(At, R(Zero256));
        var m = facts.Single(f => f.Key == MeasuredBootFactsService.MeasuredKey);

        Assert.Contains("有些平台在韌體設定裡可以關閉量測", m.UnavailableReason);
        Assert.Contains("關閉後 TPM 仍然存在", m.UnavailableReason);
    }

    [Fact]
    public void 讀不到的那幾格不得當成零()
    {
        // 只有 PCR 0 讀得到，而它是零；其餘讀不到。
        var facts = MeasuredBootFactsService.Collect(At, R(p0: Zero256));
        var e = facts.Single(f => f.Key == MeasuredBootFactsService.EvidenceKey);

        Assert.Contains("PCR 0（CRTM／韌體程式碼） 全零", e.Value);
        Assert.DoesNotContain("PCR 2", e.Value);
        Assert.DoesNotContain("PCR 4", e.Value);
    }

    // ── 證據鍵 ──────────────────────────────────────────────────────────

    [Fact]
    public void 證據鍵要說出它只讀四個不重讀24個()
    {
        var facts = MeasuredBootFactsService.Collect(At, R(p0: new string('a', 64)));
        var e = facts.Single(f => f.Key == MeasuredBootFactsService.EvidenceKey);

        Assert.Contains("判準是 PCR 的內容，不是 TPM 的存在", e.Value);
        Assert.Contains("不重讀全部 24 個", e.Value);
        Assert.Contains("tpm.pcr.*", e.Value);
    }

    [Fact]
    public void 證據鍵要逐項列出已延伸與全零()
    {
        var facts = MeasuredBootFactsService.Collect(At,
            R(p0: new string('a', 64), p4: Zero256));
        var e = facts.Single(f => f.Key == MeasuredBootFactsService.EvidenceKey);

        Assert.Contains("PCR 0（CRTM／韌體程式碼） 已延伸", e.Value);
        Assert.Contains("PCR 4（開機載入器與驅動） 全零", e.Value);
        Assert.Equal(1.0, e.NumericValue);
    }

    [Fact]
    public void 失敗路徑與成功路徑的鍵集合必須相同()
    {
        var ok = MeasuredBootFactsService.Collect(At, R(p0: "a"))
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var none = MeasuredBootFactsService.Collect(At, null)
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(ok, none);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = MeasuredBootFactsService.Collect(At, R(p0: new string('a', 64)));
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "量測開機事實產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
